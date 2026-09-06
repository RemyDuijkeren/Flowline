using System.Diagnostics;
using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.Extensions.Logging.Abstractions;
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

        from.GitRef.Should().Be("HEAD");
        to.IsWorkingTree.Should().BeTrue();
    }

    /// <summary>R3/KTD2. <c>--from</c> moves the left side only; the right side stays the files on disk.</summary>
    [Fact]
    public void ResolveSides_WithFromOnly_MovesOnlyTheLeftSide()
    {
        var (from, to) = DiffCommand.ResolveSides("v1.2.0", null);

        from.GitRef.Should().Be("v1.2.0");
        to.IsWorkingTree.Should().BeTrue();
    }

    /// <summary>R4. Two refs means the working tree is not consulted at all.</summary>
    [Fact]
    public void ResolveSides_WithBothRefs_UsesNeitherSideFromTheWorkingTree()
    {
        var (from, to) = DiffCommand.ResolveSides("v1.2.0", "main");

        from.GitRef.Should().Be("v1.2.0");
        to.GitRef.Should().Be("main");
        to.IsWorkingTree.Should().BeFalse();
    }

    /// <summary>R5/R13. The refusal names the option that is missing, because that is the corrective action.</summary>
    [Fact]
    public void ResolveSides_WithToButNoFrom_IsRejectedAsInvalidInput_NamingFrom()
    {
        var act = () => DiffCommand.ResolveSides(null, "main");

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("--from");
    }

    // ---- preconditions ------------------------------------------------------------------------

    /// <summary>R13. Outside a repository the failure says so and names 'git init'.</summary>
    [Fact]
    public void EnsureGitRepository_OutsideARepository_FailsNamingGitInit()
    {
        Directory.CreateDirectory(_root);

        var act = () => DiffCommand.EnsureGitRepository(_root);

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ConfigInvalid)
           .And.Message.Should().Contain("git init");
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

    /// <summary>R13. A solution file whose src/ was never unpacked names 'sync' as the fix.</summary>
    [Fact]
    public async Task ResolveSourceFolderAsync_WithNoUnpackedSource_FailsNamingSync()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        Directory.Delete(srcFolder, recursive: true);

        var act = () => DiffCommand.ResolveSourceFolderAsync(_root, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.NotFound)
            .And.Message.Should().Contain("sync");
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

        var exitCode = await command.DiffAsync(_root, from: null, to: null, writeTo: null, verbose: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Output.Should().Contain("Account").And.Contain("entity metadata");
    }

    /// <summary>Two identical sides report no changes and still exit 0 — nothing failed.</summary>
    [Fact]
    public async Task DiffAsync_WithIdenticalSides_ReportsNoChangesAndSucceeds()
    {
        await CreateSolutionRepoAsync();
        var (command, console) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: null, verbose: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Output.Should().Contain("No changes");
    }

    /// <summary>R4. With two refs the working tree is not consulted, so an uncommitted file is not reported.</summary>
    [Fact]
    public async Task DiffAsync_WithBothRefs_IgnoresTheWorkingTree()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, console) = MakeCommand();

        var exitCode = await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: null, verbose: false, CancellationToken.None);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Output.Should().NotContain("entity metadata");
    }

    /// <summary>R13. An unknown ref fails as a missing resource, with the command that lists the real ones.</summary>
    [Fact]
    public async Task DiffAsync_WithAnUnknownRef_FailsNamingHowToListRefs()
    {
        await CreateSolutionRepoAsync();
        var (command, _) = MakeCommand();

        var act = () => command.DiffAsync(_root, from: "no-such-ref", to: null, writeTo: null, verbose: false, CancellationToken.None);

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

        await command.DiffAsync(_root, from: "HEAD", to: null, writeTo: null, verbose: false, CancellationToken.None);

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

        await command.DiffAsync(_root, from: null, to: null, writeTo: null, verbose: false, CancellationToken.None);

        (await File.ReadAllTextAsync(existing)).Should().Be("untouched");
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length.Should().Be(before);
    }

    /// <summary>R8/KTD5. A bare --write lands CHANGES.md at the repo root, not inside the scanned source root.</summary>
    [Fact]
    public async Task DiffAsync_WithBareWrite_WritesChangesFileAtTheRepoRoot()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: Path.Combine(_root, "CHANGES.md"),
            verbose: false, CancellationToken.None);

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
            verbose: false, CancellationToken.None);

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
        await first.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, CancellationToken.None);

        var (second, console) = MakeCommand();
        await second.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, CancellationToken.None);

        console.Output.Should().NotContain("CHANGES");
    }

    /// <summary>R8. A target under a folder that doesn't exist yet gets its parent created.</summary>
    [Fact]
    public async Task DiffAsync_WithWriteUnderAMissingFolder_CreatesTheParent()
    {
        var srcFolder = await CreateSolutionRepoAsync();
        await WriteComponentAsync(srcFolder, "Entities/Account/Entity.xml", "<entity/>");
        var target = Path.Combine(_root, "reports", "diff.md");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, CancellationToken.None);

        File.Exists(target).Should().BeTrue();
    }

    /// <summary>R8. No changes still writes, recording the compared points and zero changes.</summary>
    [Fact]
    public async Task DiffAsync_WithWriteAndNoChanges_StillWritesNamingTheComparedPoints()
    {
        await CreateSolutionRepoAsync();
        var target = Path.Combine(_root, "CHANGES.md");
        var (command, _) = MakeCommand();

        await command.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: target, verbose: false, CancellationToken.None);

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
        await first.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, CancellationToken.None);
        File.Delete(Path.Combine(srcFolder, "Entities", "Account", "Entity.xml"));

        var (second, _) = MakeCommand();
        await second.DiffAsync(_root, from: "HEAD", to: "HEAD", writeTo: target, verbose: false, CancellationToken.None);

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

        await command.DiffAsync(_root, from: null, to: null, writeTo: target, verbose: false, CancellationToken.None);

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

        await command.DiffAsync(_root, from: "HEAD~1", to: "HEAD", writeTo: target, verbose: false, CancellationToken.None);

        var content = await File.ReadAllTextAsync(target);
        content.Should().Contain("Compared: HEAD~1 -> HEAD");
        content.Should().Contain("entity metadata");
    }
}
