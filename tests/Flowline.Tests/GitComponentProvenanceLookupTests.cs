using System.Diagnostics;
using FluentAssertions;
using Flowline.Core.OrphanCleanup;
using Flowline.Services;

namespace Flowline.Tests;

public class GitComponentProvenanceLookupTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-provenance-tests", Guid.NewGuid().ToString("N"));
    readonly string _srcFolder;
    readonly GitComponentProvenanceLookup _lookup;

    public GitComponentProvenanceLookupTests()
    {
        _srcFolder = Path.Combine(_root, "Solution", "src");
        Directory.CreateDirectory(_srcFolder);
        RunGit(_root, "init");
        RunGit(_root, "config", "user.email", "test@example.com");
        RunGit(_root, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, ".gitkeep"), "");
        RunGit(_root, "add", ".gitkeep");
        RunGit(_root, "commit", "-m", "init");

        _lookup = new GitComponentProvenanceLookup(_root, "Solution/src");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        Directory.Delete(_root, true);
    }

    // ── 1: file committed then removed ──────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_FileCommittedThenRemoved_ReturnsDeclaredWithRemovingCommit()
    {
        WriteFile("Roles/MyRole.xml", "<role/>");
        Commit("add role");
        var removal = CommitRemoval("Roles/MyRole.xml", "remove role");

        var result = await _lookup.ResolveAsync(ComponentSourceLocation.File("Roles/MyRole.xml"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Declared);
        result.Removal.Should().BeEquivalentTo(removal);
    }

    // ── 2: file exists, never removed ───────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_FileExistsAndNeverRemoved_ReturnsNeverInSource()
    {
        WriteFile("Roles/StillHere.xml", "<role/>");
        Commit("add role");

        var result = await _lookup.ResolveAsync(ComponentSourceLocation.File("Roles/StillHere.xml"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.NeverInSource);
        result.Removal.Should().BeNull();
    }

    // ── 3: file never existed ───────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_FileNeverExisted_ReturnsNeverInSource()
    {
        var result = await _lookup.ResolveAsync(ComponentSourceLocation.File("Roles/NeverExisted.xml"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.NeverInSource);
    }

    // ── 4: unrebasable path (locator mapped incorrectly) ────────────────────

    [Fact]
    public async Task ResolveAsync_UnknownSolutionSourceRoot_ReturnsUndeterminedWithoutTouchingGit()
    {
        var invocations = new List<IReadOnlyList<string>>();
        var lookup = new GitComponentProvenanceLookup(_root, null) { OnGitInvocation = invocations.Add };

        var result = await lookup.ResolveAsync(ComponentSourceLocation.File("Roles/Whatever.xml"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Undetermined);
        invocations.Should().BeEmpty("an unrebasable path must short-circuit before any git command runs");
    }

    // ── 5: identifier removed from its own declaration ──────────────────────

    [Fact]
    public async Task ResolveAsync_IdentifierRemovedFromOwnDeclaration_ReturnsDeclaredWithThatCommit()
    {
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute("dh_custom"));
        Commit("add attribute");
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute(null));
        Commit("remove attribute");
        var removalSha = HeadSha();
        var removal = CommitInfo(removalSha);

        var location = ComponentSourceLocation.Inline("Entities/Account/Entity.xml", "<LogicalName>dh_custom</LogicalName>");
        var result = await _lookup.ResolveAsync(location, CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Declared);
        result.Removal!.Sha.Should().Be(removalSha);
        result.Removal.Author.Should().Be(removal.Author);
        result.Removal.Subject.Should().Be(removal.Subject);
    }

    // ── 6: identifier removed only from a referencing element in a sibling file ─

    [Fact]
    public async Task ResolveAsync_IdentifierRemovedOnlyFromSiblingFormFile_ReturnsUndetermined()
    {
        // The column's own declaration in Entity.xml is committed and never touched again.
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute("dh_custom"));
        Commit("add entity attribute");

        // A separate form file references the same logical name...
        WriteFile("Entities/Account/FormXml/main/form.xml", FormXmlReferencing("dh_custom"));
        Commit("add form reference");

        // ...and later the reference (not the declaration) is removed.
        WriteFile("Entities/Account/FormXml/main/form.xml", FormXmlReferencing(null));
        Commit("remove form reference");

        var location = ComponentSourceLocation.Inline("Entities/Account/Entity.xml", "<LogicalName>dh_custom</LogicalName>");
        var result = await _lookup.ResolveAsync(location, CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Undetermined,
            "the pathspec is scoped to Entity.xml, so the sibling form file's removal must never surface as this component's removal");
    }

    // ── 7: only matching commit is an addition ──────────────────────────────

    [Fact]
    public async Task ResolveAsync_OnlyMatchingCommitIsAnAddition_ReturnsUndetermined()
    {
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute("dh_onlyadded"));
        Commit("add attribute, never removed");

        var location = ComponentSourceLocation.Inline("Entities/Account/Entity.xml", "<LogicalName>dh_onlyadded</LogicalName>");
        var result = await _lookup.ResolveAsync(location, CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Undetermined);
    }

    // ── 8: more than one candidate commit ────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_MoreThanOneRemovalCandidate_ReturnsUndetermined()
    {
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute("dh_flappy"));
        Commit("add attribute");
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute(null));
        Commit("remove attribute (1st time)");
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute("dh_flappy"));
        Commit("re-add attribute");
        WriteFile("Entities/Account/Entity.xml", EntityXmlWithAttribute(null));
        Commit("remove attribute (2nd time)");

        var location = ComponentSourceLocation.Inline("Entities/Account/Entity.xml", "<LogicalName>dh_flappy</LogicalName>");
        var result = await _lookup.ResolveAsync(location, CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Undetermined);
    }

    // ── 9: shallow checkout ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ShallowCheckout_ReturnsUndeterminedWithoutHistoryQuery()
    {
        var checkoutDir = BuildShallowCheckout();
        var invocations = new List<IReadOnlyList<string>>();
        var lookup = new GitComponentProvenanceLookup(checkoutDir, "src") { OnGitInvocation = invocations.Add };

        var result = await lookup.ResolveAsync(ComponentSourceLocation.File("a.txt"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Undetermined);
        invocations.Should().NotContain(args => args.Count > 0 && args[0] == "log",
            "a shallow checkout must never issue a history query");
    }

    // ── 10: partial-clone checkout ───────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_PartialCloneCheckout_ReturnsUndeterminedWithoutHistoryQuery()
    {
        var checkoutDir = BuildPartialCloneCheckout();
        var invocations = new List<IReadOnlyList<string>>();
        var lookup = new GitComponentProvenanceLookup(checkoutDir, "src") { OnGitInvocation = invocations.Add };

        var result = await lookup.ResolveAsync(ComponentSourceLocation.File("a.txt"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Undetermined);
        invocations.Should().NotContain(args => args.Count > 0 && args[0] == "log",
            "a partial clone must never fetch history on demand — that would need network");
    }

    // ── 11: deploy-shaped lookup, compare's source root is an unrelated temp dir ─

    [Fact]
    public async Task ResolveAsync_DeployShapedLookupWithUnknownCheckoutMapping_NeverReturnsNeverInSource()
    {
        // On deploy, CompareAsync's own source root is a temp extraction the lookup never sees — only
        // the rebased-from-checkout root matters (KTD2). When that mapping is unavailable, a path that
        // would otherwise have resolved cleanly (nothing ever existed at this path either way) must
        // still read Undetermined rather than the affirmative NeverInSource a resolvable path would get.
        var lookup = new GitComponentProvenanceLookup(_root, null);

        var result = await lookup.ResolveAsync(ComponentSourceLocation.File("Roles/NeverExisted.xml"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Undetermined);
        result.Verdict.Should().NotBe(ProvenanceVerdict.NeverInSource);
    }

    // ── 12: a failing git invocation ─────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ProjectRootIsNotAGitRepository_ReturnsUndeterminedAndDoesNotThrow()
    {
        // Deliberately NOT nested under _root (which the ctor already made a real repo) — git resolves
        // the enclosing repo by walking up from the working directory, so a nested non-repo folder would
        // just inherit _root's repo instead of proving the "no repo at all" failure this test wants.
        var notARepo = Path.Combine(Path.GetTempPath(), "flowline-provenance-tests-norepo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(notARepo);
        try
        {
            var lookup = new GitComponentProvenanceLookup(notARepo, "src");

            Func<Task> act = () => lookup.ResolveAsync(ComponentSourceLocation.File("a.txt"), CancellationToken.None);

            await act.Should().NotThrowAsync();
            var result = await lookup.ResolveAsync(ComponentSourceLocation.File("a.txt"), CancellationToken.None);
            result.Verdict.Should().Be(ProvenanceVerdict.Undetermined);
        }
        finally
        {
            Directory.Delete(notARepo, true);
        }
    }

    // ── 13: no memoisation across two lookups for the same component ────────

    [Fact]
    public async Task ResolveAsync_CalledTwiceForSameComponent_InvokesGitBothTimes()
    {
        WriteFile("Roles/StillHere.xml", "<role/>");
        Commit("add role");

        var invocations = new List<IReadOnlyList<string>>();
        var lookup = new GitComponentProvenanceLookup(_root, "Solution/src") { OnGitInvocation = invocations.Add };
        var location = ComponentSourceLocation.File("Roles/StillHere.xml");

        await lookup.ResolveAsync(location, CancellationToken.None);
        await lookup.ResolveAsync(location, CancellationToken.None);

        invocations.Count(args => args.Count > 0 && args[0] == "log").Should().Be(2,
            "the verdict is never cached (KTD6) — each resolve asks git fresh");
        invocations.Count(args => args.Count > 0 && args[0] == "rev-parse").Should().Be(1,
            "the shallow/partial probe is cached within this run only");
    }

    // ── 14: on-disk casing differs from the composed path ────────────────────

    [Fact]
    public async Task ResolveAsync_ComposedPathCasingDiffersFromDisk_StillResolves()
    {
        WriteFile("Entities/Account/Entity.xml", "<entity/>");
        Commit("add entity file");
        var removal = CommitRemoval("Entities/Account/Entity.xml", "remove entity file");

        var location = ComponentSourceLocation.File("entities/account/entity.xml");
        var result = await _lookup.ResolveAsync(location, CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Declared);
        result.Removal!.Sha.Should().Be(removal.Sha);
    }

    // ── Folder shape: define and prove the folder-removal rule ──────────────

    [Fact]
    public async Task ResolveAsync_FolderFullyRemoved_ReturnsDeclaredWithLastRemovalCommit()
    {
        WriteFile("Copilots/dh_MyCopilot/copilot.xml", "<copilot/>");
        WriteFile("Copilots/dh_MyCopilot/topic.xml", "<topic/>");
        Commit("add copilot folder");

        File.Delete(Path.Combine(_srcFolder, "Copilots", "dh_MyCopilot", "copilot.xml"));
        Commit("remove copilot.xml");

        File.Delete(Path.Combine(_srcFolder, "Copilots", "dh_MyCopilot", "topic.xml"));
        Commit("remove topic.xml — folder now empty");
        var lastRemovalSha = HeadSha();

        var result = await _lookup.ResolveAsync(ComponentSourceLocation.Folder("Copilots/dh_MyCopilot"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.Declared);
        result.Removal!.Sha.Should().Be(lastRemovalSha);
    }

    [Fact]
    public async Task ResolveAsync_FolderPartiallyRemoved_ReturnsNeverInSource()
    {
        WriteFile("Copilots/dh_OtherCopilot/copilot.xml", "<copilot/>");
        WriteFile("Copilots/dh_OtherCopilot/topic.xml", "<topic/>");
        Commit("add copilot folder");

        File.Delete(Path.Combine(_srcFolder, "Copilots", "dh_OtherCopilot", "topic.xml"));
        Commit("remove only topic.xml — copilot.xml still present");

        var result = await _lookup.ResolveAsync(ComponentSourceLocation.Folder("Copilots/dh_OtherCopilot"), CancellationToken.None);

        result.Verdict.Should().Be(ProvenanceVerdict.NeverInSource,
            "one file is still present under the folder, so the folder as a whole is not affirmatively removed");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    static string EntityXmlWithAttribute(string? logicalName) => logicalName is null
        ? "<Entity><EntityInfo><entity Name=\"Account\"><attributes></attributes></entity></EntityInfo></Entity>"
        : $"<Entity><EntityInfo><entity Name=\"Account\"><attributes><attribute><LogicalName>{logicalName}</LogicalName></attribute></attributes></entity></EntityInfo></Entity>";

    static string FormXmlReferencing(string? fieldName) => fieldName is null
        ? "<forms><systemform><rows></rows></systemform></forms>"
        : $"<forms><systemform><rows><row><cell datafieldname=\"{fieldName}\" /></row></rows></systemform></forms>";

    void WriteFile(string relPathFromSrc, string content)
    {
        var full = Path.Combine(_srcFolder, relPathFromSrc.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    void Commit(string message)
    {
        RunGit(_root, "add", "-A");
        RunGit(_root, "commit", "-m", message);
    }

    string HeadSha() => RunGitCapture(_root, "rev-parse", "HEAD").Trim();

    RemovalCommit CommitInfo(string sha) => new(
        sha,
        RunGitCapture(_root, "log", "-1", "--format=%an", sha).Trim(),
        DateTimeOffset.Parse(RunGitCapture(_root, "log", "-1", "--format=%aI", sha).Trim()),
        RunGitCapture(_root, "log", "-1", "--format=%s", sha).Trim());

    RemovalCommit CommitRemoval(string relPathFromSrc, string message)
    {
        var full = Path.Combine(_srcFolder, relPathFromSrc.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(full);
        Commit(message);
        return CommitInfo(HeadSha());
    }

    // Genuine, offline, single-machine shallow/partial checkouts — no fake state, no network. A local
    // path clone needs no "file://" scheme, which sidesteps Windows URI quirks entirely.
    string BuildShallowCheckout()
    {
        var origin = Path.Combine(_root, "shallow-origin");
        Directory.CreateDirectory(origin);
        InitRepo(origin);
        WriteFileIn(origin, "src/a.txt", "one");
        RunGit(origin, "add", "-A");
        RunGit(origin, "commit", "-m", "one");
        WriteFileIn(origin, "src/a.txt", "two");
        RunGit(origin, "add", "-A");
        RunGit(origin, "commit", "-m", "two");

        // --no-local: a plain local-path clone otherwise takes git's hardlinking fast path, which
        // silently ignores --depth and produces a full (non-shallow) clone instead.
        var checkoutDir = Path.Combine(_root, "shallow-checkout");
        RunGit(_root, "clone", "--no-local", "--depth=1", origin, checkoutDir);
        return checkoutDir;
    }

    string BuildPartialCloneCheckout()
    {
        var origin = Path.Combine(_root, "partial-origin");
        Directory.CreateDirectory(origin);
        InitRepo(origin);
        WriteFileIn(origin, "src/a.txt", "one");
        RunGit(origin, "add", "-A");
        RunGit(origin, "commit", "-m", "one");

        var checkoutDir = Path.Combine(_root, "partial-checkout");
        RunGit(_root, "clone", "--no-local", "--filter=blob:none", origin, checkoutDir);
        return checkoutDir;
    }

    static void InitRepo(string dir)
    {
        RunGit(dir, "init");
        RunGit(dir, "config", "user.email", "test@example.com");
        RunGit(dir, "config", "user.name", "Test");
    }

    static void WriteFileIn(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    static string RunGitCapture(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
