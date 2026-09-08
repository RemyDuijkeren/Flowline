using System.Diagnostics;
using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

/// <summary>
/// Covers what <c>flowline diff</c> decides before and while it reports: which two sides the options name,
/// that a git repository and an unpacked solution source are both present, and that the report renders.
/// Every path runs against a temp repository through the command's internal entry point, so none of it
/// needs the base command pipeline — and none of it constructs a Dataverse connection, which is the
/// structural form of "this command never contacts Dataverse".
/// </summary>
public class DiffCommandTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---- fixtures -----------------------------------------------------------------------------

    static (DiffCommand Command, TestConsole Console) MakeCommand()
    {
        var console = new TestConsole();
        var runtimeOptions = new FlowlineRuntimeOptions();
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, runtimeOptions);
        var capture = new SubprocessCapture(console);

        var command = new DiffCommand(console, runtimeOptions, profileResolutionService,
            NullLoggerFactory.Instance, capture, new NuGetVersionClient(new HttpClient()));

        return (command, console);
    }

    void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    // git's default initial-branch name follows the caller's global config, so the merge-base fixtures
    // below read it back instead of assuming "master" or "main".
    string CurrentBranch()
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("--abbrev-ref");
        psi.ArgumentList.Add("HEAD");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    /// <summary>A repo holding a solution file, a .cdsproj, and an unpacked source folder — the shape every
    /// command after <c>clone</c> resolves against.</summary>
    async Task<string> CreateSolutionRepoAsync(string solutionName = "Contoso")
    {
        var dataverseSolutionFolder = Path.Combine(_root, "Solution");
        var srcFolder = Path.Combine(dataverseSolutionFolder, "src", "Other");
        Directory.CreateDirectory(srcFolder);

        var cdsprojPath = Path.Combine(dataverseSolutionFolder, $"{solutionName}.cdsproj");
        await File.WriteAllTextAsync(cdsprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        await ProjectScaffolder.AddDataverseSolutionProjectAsync(
            new MsBuildSolutionWriter(), _root, Path.Combine(_root, $"{solutionName}.sln"), cdsprojPath);

        await File.WriteAllTextAsync(Path.Combine(srcFolder, "Solution.xml"), "<ImportExportXml/>");

        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
        RunGit("add", ".");
        RunGit("commit", "-m", "init");

        return Path.Combine(dataverseSolutionFolder, "src");
    }

    static Task WriteComponentAsync(string srcFolder, string relPath, string content)
    {
        var full = Path.Combine(srcFolder, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return File.WriteAllTextAsync(full, content);
    }

    // ---- side resolution ----------------------------------------------------------------------

    /// <summary>R2. No options is HEAD against the working tree.</summary>
    [Fact]
    public void ResolveSides_WithNoOptions_IsHeadAgainstTheWorkingTree()
    {
        var (from, to) = DiffCommand.ResolveSides(null, null);

        from.Should().Be("HEAD");
        to.IsWorkingTree.Should().BeTrue();
    }

    /// <summary>R3/KTD2. <c>--from</c> moves the left side only; the right side stays the files on disk.</summary>
    [Fact]
    public void ResolveSides_WithFromOnly_MovesOnlyTheLeftSide()
    {
        var (from, to) = DiffCommand.ResolveSides("v1.2.0", null);

        from.Should().Be("v1.2.0");
        to.IsWorkingTree.Should().BeTrue();
    }

    /// <summary>R4. Two refs means the working tree is not consulted at all.</summary>
    [Fact]
    public void ResolveSides_WithBothRefs_UsesNeitherSideFromTheWorkingTree()
    {
        var (from, to) = DiffCommand.ResolveSides("v1.2.0", "main");

        from.Should().Be("v1.2.0");
        to.GitRef.Should().Be("main");
        to.IsWorkingTree.Should().BeFalse();
    }

    // ---- ref-spec parsing (R28) -----------------------------------------------------------------

    /// <summary>No args: HEAD against the working tree, same as ResolveSides' own default.</summary>
    [Fact]
    public void ParseRefSpec_WithNoArgs_IsHeadAgainstTheWorkingTree()
    {
        var spec = DiffCommand.ParseRefSpec(null, null);

        spec.From.Should().Be("HEAD");
        spec.To.Should().BeNull();
        spec.MergeBase.Should().BeFalse();
    }

    /// <summary>One ref: that ref against the working tree.</summary>
    [Fact]
    public void ParseRefSpec_WithOneArg_IsThatRefAgainstTheWorkingTree()
    {
        var spec = DiffCommand.ParseRefSpec("v1.2.0", null);

        spec.From.Should().Be("v1.2.0");
        spec.To.Should().BeNull();
        spec.MergeBase.Should().BeFalse();
    }

    /// <summary>Two refs compare the refs, and neither is a merge-base comparison.</summary>
    [Fact]
    public void ParseRefSpec_WithTwoArgs_ComparesBothRefsDirectly()
    {
        var spec = DiffCommand.ParseRefSpec("v1.2.0", "v1.3.0");

        spec.From.Should().Be("v1.2.0");
        spec.To.Should().Be("v1.3.0");
        spec.MergeBase.Should().BeFalse();
    }

    /// <summary>'A..B' is shorthand for 'A B' — a plain two-ref comparison, not a merge base.</summary>
    [Fact]
    public void ParseRefSpec_WithTwoDotRange_SplitsIntoTheSameTwoRefs()
    {
        var spec = DiffCommand.ParseRefSpec("v1.2.0..v1.3.0", null);

        spec.From.Should().Be("v1.2.0");
        spec.To.Should().Be("v1.3.0");
        spec.MergeBase.Should().BeFalse();
    }

    /// <summary>'A...B' names a merge-base comparison — resolved to an actual SHA later, not here.</summary>
    [Fact]
    public void ParseRefSpec_WithThreeDotRange_SplitsIntoBothRefsAndFlagsMergeBase()
    {
        var spec = DiffCommand.ParseRefSpec("v1.2.0...v1.3.0", null);

        spec.From.Should().Be("v1.2.0");
        spec.To.Should().Be("v1.3.0");
        spec.MergeBase.Should().BeTrue();
    }

    /// <summary>A range positional plus a second positional names the right side twice — refused.</summary>
    [Fact]
    public void ParseRefSpec_WithARangeAndASecondArg_IsRejectedAsInvalidInput()
    {
        var act = () => DiffCommand.ParseRefSpec("A..B", "C");

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("A..B").And.Contain("C");
    }

    /// <summary>'..B' has no left side — refused, naming the missing side.</summary>
    [Fact]
    public void ParseRefSpec_WithATwoDotRangeMissingTheLeftSide_IsRejectedNamingIt()
    {
        var act = () => DiffCommand.ParseRefSpec("..B", null);

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("before");
    }

    /// <summary>'A..' has no right side — refused, naming the missing side.</summary>
    [Fact]
    public void ParseRefSpec_WithATwoDotRangeMissingTheRightSide_IsRejectedNamingIt()
    {
        var act = () => DiffCommand.ParseRefSpec("A..", null);

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("after");
    }

    // ---- preconditions ------------------------------------------------------------------------

    /// <summary>R13. Outside a repository the failure says so and names 'git init'.</summary>
    [Fact]
    public async Task EnsureGitRepositoryAsync_OutsideARepository_FailsNamingGitInit()
    {
        Directory.CreateDirectory(_root);

        var act = () => DiffCommand.EnsureGitRepositoryAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ConfigInvalid)
            .And.Message.Should().Contain("git init");
    }

    /// <summary>R13. A .git file pointing at a gitdir that isn't there exists on disk but isn't a repository,
    /// so the prerequisite has to be answered by git, not by the filesystem.</summary>
    [Fact]
    public async Task EnsureGitRepositoryAsync_WithAStaleWorktreePointer_FailsAsAMissingPrerequisite()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, ".git"),
            "gitdir: " + Path.Combine(_root, "no-such-gitdir"));

        var act = () => DiffCommand.EnsureGitRepositoryAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    /// <summary>R13. No solution file means the failure names the solution file, not the missing source.</summary>
    [Fact]
    public async Task ResolveSourceFolderAsync_WithNoSolutionFile_FailsNamingTheSolutionFile()
    {
        Directory.CreateDirectory(_root);

        var act = () => DiffCommand.ResolveSourceFolderAsync(_root, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.NotFound)
            .And.Message.Should().Contain("solution file");
    }

    /// <summary>R13. A solution file whose src/ was never unpacked names 'pull' as the fix.</summary>
    [Fact]
    public async Task ResolveSourceFolderAsync_WithNoUnpackedSource_FailsNamingPull()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        Directory.Delete(srcFolder, recursive: true);

        var act = () => DiffCommand.ResolveSourceFolderAsync(_root, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.NotFound)
            .And.Message.Should().Contain("pull");
    }

    /// <summary>The source folder is read through the solution file, so a relocated solution folder is followed.</summary>
    [Fact]
    public async Task ResolveSourceFolderAsync_WithASolutionFile_ResolvesSrcUnderTheDataverseProject()
    {
        var expected = await CreateSolutionRepoAsync();

        var (resolved, solutionName) = await DiffCommand.ResolveSourceFolderAsync(_root, CancellationToken.None);

        resolved.Should().Be(expected);
        solutionName.Should().Be("Contoso");
    }

    // ---- reporting ----------------------------------------------------------------------------

    /// <summary>R1/R2. A bare run in a project repo reports the components that changed, counting an
    /// untracked file as an addition.</summary>
    [Fact]
    public async Task DiffAsync_WithNoOptions_ReportsUncommittedAndUntrackedChanges()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, console) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: null, to: null, writeTo: null, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Output.Should().Contain("Account").And.Contain("entity metadata");
    }

    /// <summary>Two identical sides report no changes and still exit 0 — nothing failed.</summary>
    [Fact]
    public async Task DiffAsync_WithIdenticalSides_ReportsNoChangesAndSucceeds()
    {
        await CreateSolutionRepoAsync();
        var (command, console) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: null, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Output.Should().Contain("No changes");
    }

    /// <summary>R4. With two refs the working tree is not consulted: the committed change between the two
    /// refs is reported and the uncommitted one sitting on disk is not.</summary>
    [Fact]
    public async Task DiffAsync_WithBothRefs_ReportsTheCommittedChangeAndIgnoresTheWorkingTree()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        RunGit("add", ".");
        RunGit("commit", "-m", "account");
        await WriteComponentAsync(srcFolder, "Entities/Contact/Entity.xml", "<entity/>");
        var (command, console) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: "HEAD~1", to: "HEAD", writeTo: null, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Output.Should().Contain("Account");
        console.Output.Should().NotContain("Contact");
    }

    /// <summary>AE10. A three-dot range reports only the changes made after the two branches' shared base —
    /// not the change that landed on the base branch after they split.</summary>
    [Fact]
    public async Task DiffAsync_WithThreeDotRange_ReportsOnlyChangesAfterTheMergeBase()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        var trunk = CurrentBranch();
        RunGit("branch", "release");
        RunGit("checkout", "release");
        await WriteComponentAsync(srcFolder, "Entities/Contact/Entity.xml", "<entity/>");
        RunGit("add", ".");
        RunGit("commit", "-m", "release-only change");
        RunGit("checkout", trunk);
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        RunGit("add", ".");
        RunGit("commit", "-m", "main-only change");
        var (command, console) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: $"{trunk}...release", to: null, writeTo: null,
            verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Output.Should().Contain("Contact");
        console.Output.Should().NotContain("Account");
    }

    /// <summary>AE10. Two refs sharing no common history fail, naming both refs.</summary>
    [Fact]
    public async Task DiffAsync_WithThreeDotRangeAndNoCommonBase_FailsNamingBothRefs()
    {
        await CreateSolutionRepoAsync();
        var trunk = CurrentBranch();
        RunGit("checkout", "--orphan", "unrelated");
        RunGit("commit", "--allow-empty", "-m", "unrelated root");
        var (command, _) = MakeCommand();

        var act = () => command.DiffAsync(_root, from: $"{trunk}...unrelated", to: null, writeTo: null,
            verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .And.Message.Should().Contain(trunk).And.Contain("unrelated");
    }

    /// <summary>R13. An unknown ref fails as a missing resource, with the command that lists the real ones.</summary>
    [Fact]
    public async Task DiffAsync_WithAnUnknownRef_FailsNamingHowToListRefs()
    {
        await CreateSolutionRepoAsync();
        var (command, _) = MakeCommand();

        var act = () => command.DiffAsync(_root, from: "no-such-ref", to: null, writeTo: null, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.NotFound)
            .And.Message.Should().Contain("git log");
    }

    /// <summary>R10/KTD6. The terminal no-changes line names the two compared points and no environment —
    /// this command contacts none.</summary>
    [Fact]
    public async Task DiffAsync_WithNoChanges_NamesTheComparedPointsAndNoEnvironment()
    {
        await CreateSolutionRepoAsync();
        var (command, console) = MakeCommand();

        await command.DiffAsync(_root, from: "HEAD", to: null, writeTo: null, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        console.Output.Should().Contain("HEAD").And.Contain("working tree");
        console.Output.Should().NotContain("DEV");
    }

    // ---- --write ------------------------------------------------------------------------------

    /// <summary>R9. Without --write the command creates nothing and leaves an existing CHANGES.md alone.</summary>
    [Fact]
    public async Task DiffAsync_WithoutWrite_CreatesNoFileAndLeavesAnExistingReportAlone()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var existing = Path.Combine(_root, "CHANGES.md");
        await File.WriteAllTextAsync(existing, "untouched");
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length;
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: null, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        (await File.ReadAllTextAsync(existing)).Should().Be("untouched");
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length.Should().Be(before);
    }

    /// <summary>R8/KTD5. The default target lands CHANGES.md at the project root, not inside the scanned
    /// source root.</summary>
    [Fact]
    public async Task DiffAsync_WithTheDefaultTarget_WritesChangesFileAtTheProjectRoot()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: Path.Combine(_root, "CHANGES.md"),
            verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_root, "CHANGES.md"));
        content.Should().Contain("entity metadata");
        File.Exists(Path.Combine(srcFolder, "CHANGES.md")).Should().BeFalse();
    }

    /// <summary>R8. A named target is written and CHANGES.md is left out of it entirely.</summary>
    [Fact]
    public async Task DiffAsync_WithNamedWriteTarget_WritesThatFileOnly()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: Path.Combine(_root, "report.md"),
            verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        File.Exists(Path.Combine(_root, "report.md")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "CHANGES.md")).Should().BeFalse();
    }

    /// <summary>KTD5. The report lands outside the scanned source root, so a second run doesn't report the
    /// report itself as an untracked addition.</summary>
    [Fact]
    public async Task DiffAsync_WithWrite_DoesNotReportItsOwnFileOnTheNextRun()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var target = Path.Combine(_root, "CHANGES.md");
        var (first, _) = MakeCommand();
        await first.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        var (second, console) = MakeCommand();
        await second.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        // Still the one entity file — the report itself is outside the scanned source, so it never joins
        // the listing. (The trailing "Wrote CHANGES.md" line names the target, which is the point of it.)
        console.Output.Should().Contain("Changes (1 file");
    }

    /// <summary>R8. A target under a folder that doesn't exist yet gets its parent created.</summary>
    [Fact]
    public async Task DiffAsync_WithWriteUnderAMissingFolder_CreatesTheParent()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var target = Path.Combine(_root, "reports", "diff.md");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        File.Exists(target).Should().BeTrue();
    }

    /// <summary>R8. No changes still writes, recording the compared points and zero changes.</summary>
    [Fact]
    public async Task DiffAsync_WithWriteAndNoChanges_StillWritesNamingTheComparedPoints()
    {
        await CreateSolutionRepoAsync();
        var target = Path.Combine(_root, "CHANGES.md");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        var content = await File.ReadAllTextAsync(target);
        content.Should().Contain("Compared: HEAD -> HEAD");
        content.Should().Contain("No changes.");
    }

    /// <summary>R8. Writing over an earlier report replaces it rather than leaving stale content behind.</summary>
    [Fact]
    public async Task DiffAsync_WithWriteOverAnEarlierReport_ReplacesItsContent()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        var target = Path.Combine(_root, "CHANGES.md");
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (first, _) = MakeCommand();
        await first.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);
        File.Delete(Path.Combine(srcFolder, "Entities", "Account", "Entity.xml"));

        var (second, _) = MakeCommand();
        await second.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        (await File.ReadAllTextAsync(target)).Should().NotContain("entity metadata");
    }

    /// <summary>R10. The file records which two points were compared — working-tree mode.</summary>
    [Fact]
    public async Task DiffAsync_WithWrite_RecordsTheComparedPointsInWorkingTreeMode()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var target = Path.Combine(_root, "CHANGES.md");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        var content = await File.ReadAllTextAsync(target);
        content.Should().Contain("Compared: HEAD -> working tree");
        content.Should().NotContain("Synced from");
    }

    /// <summary>R10. The file records which two points were compared — ref-to-ref mode.</summary>
    [Fact]
    public async Task DiffAsync_WithWrite_RecordsTheComparedPointsInRefToRefMode()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        RunGit("add", ".");
        RunGit("commit", "-m", "second");
        var target = Path.Combine(_root, "CHANGES.md");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: "HEAD~1", to: "HEAD", writeTo: target, verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        var content = await File.ReadAllTextAsync(target);
        content.Should().Contain("Compared: HEAD~1 -> HEAD");
        content.Should().Contain("entity metadata");
    }

    /// <summary>The report is announced by path, because that's the file an agent reads next.</summary>
    [Fact]
    public async Task DiffAsync_WithWrite_NamesTheFileItWrote()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, console) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: Path.Combine(_root, "report.md"),
            verbose: false, exitCodeOnChanges: false, CancellationToken.None);

        console.Output.Should().Contain("report.md");
    }

    /// <summary>The bare default is the same CHANGES.md sync writes, so a run that found nothing leaves it
    /// alone rather than replacing sync's report with an empty one.</summary>
    [Fact]
    public async Task DiffAsync_WithTheBareDefaultAndNoChanges_LeavesAnExistingReportAlone()
    {
        await CreateSolutionRepoAsync();
        var target = Path.Combine(_root, "CHANGES.md");
        await File.WriteAllTextAsync(target, "sync's report");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: target, verbose: false,
            exitCodeOnChanges: false, CancellationToken.None, bareWrite: true);

        (await File.ReadAllTextAsync(target)).Should().Be("sync's report");
    }

    /// <summary>A named target still records an empty comparison — regenerating notes over an unchanged
    /// range must not leave a stale file looking current.</summary>
    [Fact]
    public async Task DiffAsync_WithANamedTargetAndNoChanges_StillRewritesIt()
    {
        await CreateSolutionRepoAsync();
        var target = Path.Combine(_root, "report.md");
        await File.WriteAllTextAsync(target, "stale");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: target, verbose: false,
            exitCodeOnChanges: false, CancellationToken.None);

        (await File.ReadAllTextAsync(target)).Should().Contain("No changes.");
    }

    /// <summary>KTD5. A target inside the compared source is refused: the next run would report it as a
    /// change, pinning --exit-code to ChangesFound forever.</summary>
    [Fact]
    public async Task DiffAsync_WithWriteInsideTheComparedSource_IsRejectedAsInvalidInput()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        var (command, _) = MakeCommand();

        var act = () => command.DiffAsync(_root, from: null, to: null,
            writeTo: Path.Combine(srcFolder, "Other", "CHANGES.md"), verbose: false,
            exitCodeOnChanges: false, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .And.Message.Should().Contain("--write");
    }

    // ---- --write: option to path ----------------------------------------------------------------

    /// <summary>Without the flag there is no target at all.</summary>
    [Fact]
    public void ResolveWriteTarget_WithoutTheFlag_IsNoTarget() =>
        DiffCommand.ResolveWriteTarget(isSet: false, value: null, @"C:\project").Should().BeNull();

    /// <summary>A bare flag anchors CHANGES.md to the project root, so running from a subfolder still lands
    /// it beside the solution.</summary>
    [Fact]
    public void ResolveWriteTarget_Bare_AnchorsChangesFileToTheProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-root");

        var target = DiffCommand.ResolveWriteTarget(isSet: true, value: null, root);

        target!.Value.Path.Should().Be(Path.Combine(root, "CHANGES.md"));
        target.Value.Bare.Should().BeTrue();
    }

    /// <summary>A blank value carries no path, so it's the bare flag rather than a crash on ''.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveWriteTarget_WithABlankValue_BehavesAsTheBareFlag(string value)
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-root");

        var target = DiffCommand.ResolveWriteTarget(isSet: true, value, root);

        target!.Value.Path.Should().Be(Path.Combine(root, "CHANGES.md"));
        target.Value.Bare.Should().BeTrue();
    }

    /// <summary>An explicit relative path resolves against the folder the user typed it in, matching
    /// generate/push/sln add — not against the project root.</summary>
    [Fact]
    public void ResolveWriteTarget_WithAnExplicitRelativePath_ResolvesAgainstTheCurrentFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-root");

        var target = DiffCommand.ResolveWriteTarget(isSet: true, "report.md", root);

        target!.Value.Path.Should().Be(Path.GetFullPath("report.md"));
        target.Value.Path.Should().NotBe(Path.Combine(root, "report.md"));
        target.Value.Bare.Should().BeFalse();
    }

    /// <summary>An absolute path is taken as given.</summary>
    [Fact]
    public void ResolveWriteTarget_WithAnAbsolutePath_TakesItAsGiven()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "report.md");

        var target = DiffCommand.ResolveWriteTarget(isSet: true, absolute, @"C:\project");

        target!.Value.Path.Should().Be(absolute);
        target.Value.Bare.Should().BeFalse();
    }

    // Goes through real Spectre binding: --write is a FlagValue<string>, and only the parser can say
    // whether a bare flag arrives as IsSet with a null value or never binds at all.
    sealed class DiffProbeCommand : Command<DiffCommand.Settings>
    {
        public static DiffCommand.Settings? Captured;

        protected override int Execute(CommandContext context, DiffCommand.Settings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    static DiffCommand.Settings BindDiff(params string[] args)
    {
        var app = new CommandApp<DiffProbeCommand>();
        app.Configure(config => config.PropagateExceptions());
        app.Run(args).Should().Be(0);
        return DiffProbeCommand.Captured!;
    }

    [Fact]
    public void WriteFlag_Bare_BindsAsSetWithNoValue()
    {
        var settings = BindDiff("--write");

        settings.Write.IsSet.Should().BeTrue();
        DiffCommand.ResolveWriteTarget(settings.Write.IsSet, settings.Write.Value, @"C:\project")!
                   .Value.Bare.Should().BeTrue();
    }

    [Fact]
    public void WriteFlag_WithAValue_BindsThatValue()
    {
        var settings = BindDiff("--write", "report.md");

        settings.Write.IsSet.Should().BeTrue();
        settings.Write.Value.Should().Be("report.md");
    }

    [Fact]
    public void WriteFlag_Absent_BindsAsNotSet() => BindDiff().Write.IsSet.Should().BeFalse();

    // ---- positional binding (R28/R10) ----------------------------------------------------------
    // Goes through real Spectre binding, same as the --write probes above: only the parser can say
    // whether [CommandArgument(0/1)] actually wires up two positionals rather than options.

    [Fact]
    public void Positionals_WithTwoRefs_BindFromAndTo()
    {
        var settings = BindDiff("v1.2.0", "v1.3.0");

        settings.From.Should().Be("v1.2.0");
        settings.To.Should().Be("v1.3.0");
    }

    /// <summary>A range stays one token at the binding layer — ParseRefSpec is what splits it later.</summary>
    [Fact]
    public void Positionals_WithAThreeDotRange_BindsItWholeAsFrom()
    {
        var settings = BindDiff("v1.2.0...v1.3.0");

        settings.From.Should().Be("v1.2.0...v1.3.0");
        settings.To.Should().BeNull();
    }

    // ---- --exit-code --------------------------------------------------------------------------

    /// <summary>R11. Without --exit-code, finding changes is not a failure — the run exits 0.</summary>
    [Fact]
    public async Task DiffAsync_WithChangesAndNoExitCodeOption_Succeeds()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, _) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: null, to: null, writeTo: null, verbose: false,
            exitCodeOnChanges: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
    }

    /// <summary>R11/KTD3. With --exit-code, at least one changed file exits ChangesFound.</summary>
    [Fact]
    public async Task DiffAsync_WithChangesAndExitCodeOption_ExitsChangesFound()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, _) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: null, to: null, writeTo: null, verbose: false,
            exitCodeOnChanges: true, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.ChangesFound);
    }

    /// <summary>R11/R28. --exit-code still applies when both sides are given as positionals.</summary>
    [Fact]
    public async Task DiffAsync_WithTwoPositionalsAndExitCodeOption_ExitsChangesFound()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        RunGit("add", ".");
        RunGit("commit", "-m", "account");
        var (command, _) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: "HEAD~1", to: "HEAD", writeTo: null, verbose: false,
            exitCodeOnChanges: true, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.ChangesFound);
    }

    /// <summary>R11. With --exit-code and nothing changed, the run still exits 0.</summary>
    [Fact]
    public async Task DiffAsync_WithNoChangesAndExitCodeOption_Succeeds()
    {
        await CreateSolutionRepoAsync();
        var (command, _) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: null, verbose: false,
            exitCodeOnChanges: true, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
    }

    /// <summary>R11. --exit-code changes the no-changes signal only — a real failure keeps its own code.</summary>
    [Fact]
    public async Task DiffAsync_WithExitCodeOptionAndAnUnknownRef_StillFailsAsNotFound()
    {
        await CreateSolutionRepoAsync();
        var (command, _) = MakeCommand();

        var act = () => command.DiffAsync(_root, from: "no-such-ref", to: null, writeTo: null, verbose: false,
            exitCodeOnChanges: true, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.NotFound);
    }
}
public class DiffCommandOverflowHintTests
{
    [Fact]
    public void OverflowHint_WithoutAWriteTarget_NamesTheFlagRatherThanAFile()
    {
        // The bug this replaced: diff pointed at CHANGES.md on a run that wrote nothing.
        var hint = DiffCommand.OverflowHint(null);

        hint.Should().NotContain("CHANGES.md");
        hint.Should().Contain("--write");
    }

    [Fact]
    public void OverflowHint_WithAWriteTarget_NamesThatFile()
    {
        DiffCommand.OverflowHint(Path.Combine("C:", "repo", "RELEASENOTES.md"))
            .Should().Be("see RELEASENOTES.md");
    }
}
