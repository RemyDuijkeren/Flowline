using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;

namespace Flowline.Tests;

/// <summary>
/// Covers what <c>flowline scaffold</c> decides before it writes: whether the part is one it can write,
/// whether <c>--name</c> produces a usable project, which folder the project belongs in, and which solution
/// file it is added to. The decisions are static and side-effect free, so every branch is reachable
/// without constructing the command or a console.
/// </summary>
public class ScaffoldCommandTests
{
    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Resolves the target the same way the command does, so the tests exercise that path too.</summary>
    static ScaffoldCommand.ScaffoldTarget Target(string root) => ScaffoldCommand.ResolveTarget(root);

    static (ScaffoldCommand Command, TestConsole Console) MakeCommand()
    {
        var console = new TestConsole();
        var runtimeOptions = new FlowlineRuntimeOptions();
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, runtimeOptions);
        var capture = new SubprocessCapture(console);

        var command = new ScaffoldCommand(console, runtimeOptions, profileResolutionService,
            NullLoggerFactory.Instance, capture, new ProjectScaffolder(console, capture),
            new NuGetVersionClient(new HttpClient()));

        return (command, console);
    }

    [Theory]
    [InlineData("webresources")]
    [InlineData("WebResources")]
    [InlineData("WEBRESOURCES")]
    public void ValidatePart_AcceptsWebResources_RegardlessOfCasing(string part)
    {
        var act = () => ScaffoldCommand.ValidatePart(part);

        act.Should().NotThrow();
    }

    /// <summary>Covers AE5. The error names what is accepted, because that is how an agent reading a failed
    /// run discovers the vocabulary.</summary>
    [Fact]
    public void ValidatePart_RejectsAnUnknownPart_NamingWhatIsAccepted()
    {
        var act = () => ScaffoldCommand.ValidatePart("plugins");

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("webresources");
    }

    // ---- name and root resolution -------------------------------------------------------------

    /// <summary>Covers AE12. With no solution file to name it after, the project file takes the generic name
    /// and the folder keeps the default.</summary>
    [Fact]
    public void ResolveNames_WithNoSolutionFile_IsTheGenericProjectNameInTheDefaultFolder()
    {
        var (folderName, projectFileName) = ScaffoldCommand.ResolveNames(name: null, solutionFilePath: null);

        folderName.Should().Be("WebResources");
        projectFileName.Should().Be("WebResources.csproj");
    }

    /// <summary>Covers AE13. The name comes from the solution <em>file</em>, not from config — that is what
    /// removes the <c>.flowline</c> read.</summary>
    [Theory]
    [InlineData("Contoso.slnx")]
    [InlineData("Contoso.sln")]
    public void ResolveNames_WithASolutionFile_NamesTheProjectAfterIt(string solutionFileName)
    {
        var (folderName, projectFileName) = ScaffoldCommand.ResolveNames(name: null, solutionFilePath: Path.Combine("C:", "repo", solutionFileName));

        folderName.Should().Be("WebResources");
        projectFileName.Should().Be("Contoso.WebResources.csproj");
    }

    /// <summary>Covers AE14. <c>--name</c> wins over the solution-derived name and names the folder too, so
    /// the project file and the folder around it never disagree.</summary>
    [Fact]
    public void ResolveNames_WithAName_NamesBothTheFolderAndTheProjectFile()
    {
        var (folderName, projectFileName) = ScaffoldCommand.ResolveNames("Scripts", Path.Combine("C:", "repo", "Contoso.slnx"));

        folderName.Should().Be("Scripts");
        projectFileName.Should().Be("Scripts.csproj");
    }

    /// <summary>Covers AE16. A project file ending in Test or Tests is eliminated by the WebResources
    /// resolver before scoring, so push would skip its web resources on every run. Rejecting the name at
    /// creation is the only point where that is still cheap to fix.</summary>
    [Theory]
    [InlineData("ScriptTest")]
    [InlineData("ScriptTests")]
    [InlineData("scripttests")]
    public void ValidateName_RejectsANameFlowlineWouldReadAsATestProject(string name)
    {
        var act = () => ScaffoldCommand.ValidateName(name);

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("test project");
    }

    /// <summary>Covers AE17. <c>--name</c> names one folder inside the target; a path there would let it
    /// disagree with <c>--output</c> about where the scaffold lands.</summary>
    [Fact]
    public void ValidateName_RejectsAPath_NamingTheFlagThatTakesOne()
    {
        var act = () => ScaffoldCommand.ValidateName(Path.Combine("src", "Scripts"));

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("--output");
    }

    [Fact]
    public void ValidateName_WithNoName_Passes()
    {
        var act = () => ScaffoldCommand.ValidateName(null);

        act.Should().NotThrow();
    }

    /// <summary>Covers AE15. <c>--output</c> resolves to an absolute folder and creates nothing, so a run
    /// that stops on a validation failure leaves no empty folder behind.</summary>
    [Fact]
    public void ResolveRoot_WithAnOutputPath_ResolvesItWithoutCreatingIt()
    {
        var root = CreateTempRoot();
        try
        {
            var target = Path.Combine(root, "elsewhere");

            var resolved = ScaffoldCommand.ResolveRoot(target);

            resolved.Should().Be(target);
            Directory.Exists(target).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveRoot_WithNoOutputPath_IsTheWorkingDirectory()
    {
        ScaffoldCommand.ResolveRoot(null).Should().Be(Directory.GetCurrentDirectory());
    }

    /// <summary>Covers AE20. A file where the scaffold folder would go fails with an exit code rather than
    /// the raw IOException the template writer would otherwise throw.</summary>
    [Fact]
    public void ResolveRoot_WithAnOutputPathThatIsAFile_FailsWithWriteTargetOccupied()
    {
        var root = CreateTempRoot();
        try
        {
            var file = Path.Combine(root, "notafolder.txt");
            File.WriteAllText(file, "");

            var act = () => ScaffoldCommand.ResolveRoot(file);

            act.Should().Throw<FlowlineException>()
               .Where(e => e.ExitCode == ExitCode.WriteTargetOccupied)
               .And.Message.Should().Contain("--output");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- bounded upward search ----------------------------------------------------------------

    static void WriteSolutionFile(string folder, string name = "Contoso.slnx")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), "<Solution />");
    }

    /// <summary>Covers AE21. A solution file in the folder you are standing in wins without any walk.</summary>
    [Fact]
    public void ResolveTarget_WithASolutionFileInTheStartFolder_UsesIt()
    {
        var root = CreateTempRoot();
        try
        {
            WriteSolutionFile(root);

            var target = ScaffoldCommand.ResolveTarget(root);

            target.Folder.Should().Be(root);
            target.SolutionFilePath.Should().Be(Path.Combine(root, "Contoso.slnx"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE22. Run from a subfolder of a repo, the solution file two levels up is found — and
    /// the project is still written where the user is standing, not moved up beside it.</summary>
    [Theory]
    [InlineData(".git")]
    [InlineData(".flowline")]
    public void ResolveTarget_FromASubfolder_FindsTheSolutionFileButStaysWhereItStarted(string marker)
    {
        var root = CreateTempRoot();
        try
        {
            WriteBoundaryMarker(root, marker);
            WriteSolutionFile(root);
            var nested = Path.Combine(root, "Plugins", "Handlers");
            Directory.CreateDirectory(nested);

            var target = ScaffoldCommand.ResolveTarget(nested);

            target.Folder.Should().Be(nested);
            target.SolutionFilePath.Should().Be(Path.Combine(root, "Contoso.slnx"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static void WriteBoundaryMarker(string folder, string marker)
    {
        if (marker == ".git")
            Directory.CreateDirectory(Path.Combine(folder, ".git"));
        else
            File.WriteAllText(Path.Combine(folder, marker), "{}");
    }

    /// <summary>A linked worktree and a submodule carry a `.git` <em>file</em>, not a folder, and working
    /// from a worktree is normal.</summary>
    [Fact]
    public void ResolveTarget_WithAGitFileRatherThanAFolder_StillBoundsTheSearch()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../.git/worktrees/x");
            WriteSolutionFile(root);
            var nested = Path.Combine(root, "Plugins");
            Directory.CreateDirectory(nested);

            ScaffoldCommand.ResolveTarget(nested).SolutionFilePath.Should().Be(Path.Combine(root, "Contoso.slnx"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE23. The boundary is where the search stops: a solution file above the repo root
    /// belongs to different work and is never reached.</summary>
    [Fact]
    public void ResolveTarget_WithASolutionFileAboveTheBoundary_DoesNotReachIt()
    {
        var outer = CreateTempRoot();
        try
        {
            WriteSolutionFile(outer, "Unrelated.slnx");
            var repo = Path.Combine(outer, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var nested = Path.Combine(repo, "Plugins");
            Directory.CreateDirectory(nested);

            var target = ScaffoldCommand.ResolveTarget(nested);

            target.SolutionFilePath.Should().BeNull();
            target.Folder.Should().Be(nested);
        }
        finally { Directory.Delete(outer, recursive: true); }
    }

    /// <summary>Covers AE24. Stand-alone use has no repo and no config, so there is nothing to bound a walk
    /// — and an unbounded one would climb to the drive root and write itself into whatever it met first.</summary>
    [Fact]
    public void ResolveTarget_WithNoBoundaryMarkerAnywhere_DoesNotWalkUpAtAll()
    {
        var outer = CreateTempRoot();
        try
        {
            WriteSolutionFile(outer, "Unrelated.slnx");
            var nested = Path.Combine(outer, "somewhere", "deep");
            Directory.CreateDirectory(nested);

            var target = ScaffoldCommand.ResolveTarget(nested);

            target.SolutionFilePath.Should().BeNull();
            target.Folder.Should().Be(nested);
        }
        finally { Directory.Delete(outer, recursive: true); }
    }

    /// <summary>A bounded search that finds no solution file reports none, rather than climbing past the
    /// repo root looking for one.</summary>
    [Fact]
    public void ResolveTarget_WithABoundaryButNoSolutionFile_StaysWhereItStarted()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var nested = Path.Combine(root, "Plugins");
            Directory.CreateDirectory(nested);

            var target = ScaffoldCommand.ResolveTarget(nested);

            target.SolutionFilePath.Should().BeNull();
            target.Folder.Should().Be(nested);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- writing ------------------------------------------------------------------------------

    /// <summary>Covers AE1. A folder with no solution file gets the template under the generic project name
    /// and nothing else — no solution file, no config.</summary>
    [Fact]
    public async Task Scaffold_WithNoSolutionFile_WritesTheTemplateUnderTheGenericProjectName()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, _) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            var webResources = Path.Combine(root, "WebResources");
            File.Exists(Path.Combine(webResources, "WebResources.csproj")).Should().BeTrue();
            File.Exists(Path.Combine(webResources, "package.json")).Should().BeTrue();
            File.Exists(Path.Combine(webResources, "src", "example.ts")).Should().BeTrue();
            Directory.Exists(Path.Combine(webResources, "dist")).Should().BeTrue();

            Directory.EnumerateFiles(root, "*.slnx").Should().BeEmpty();
            File.Exists(Path.Combine(root, ".flowline")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE25. A project outside the solution file is invisible to every later command, so the run says so
    /// without needing --verbose.</summary>
    [Fact]
    public async Task Scaffold_WithNoSolutionFile_SkipsAddingToASolutionFile()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, console) = MakeCommand();

            await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            console.Output.Should().Contain("No solution file found to add project to");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE15. The scaffold lands in the folder it was given, which is created on the way.</summary>
    [Fact]
    public async Task Scaffold_WithATargetFolderThatDoesNotExist_CreatesItAndWritesThere()
    {
        var root = CreateTempRoot();
        try
        {
            var target = Path.Combine(root, "new", "repo");
            var (command, _) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(target), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.Exists(Path.Combine(target, "WebResources", "WebResources.csproj")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, "WebResources")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE14. <c>--name</c> reaches disk: the folder and the project file both carry it.</summary>
    [Fact]
    public async Task Scaffold_WithAName_WritesTheTemplateIntoAFolderOfThatName()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, console) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), "Scripts", CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.Exists(Path.Combine(root, "Scripts", "Scripts.csproj")).Should().BeTrue();
            File.Exists(Path.Combine(root, "Scripts", "package.json")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, "WebResources")).Should().BeFalse();
            // "WebResources project created" above a Scripts.csproj is a line the user has to reconcile.
            console.Output.Should().Contain("Scripts project created");
            console.Output.Should().NotContain("WebResources project created");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE3. A second run is a reporting no-op, even when the user has edited the template.</summary>
    [Fact]
    public async Task Scaffold_RunAgainOverAnEditedTemplate_SkipsAndChangesNothing()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, console) = MakeCommand();
            await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            var edited = Path.Combine(root, "WebResources", "package.json");
            File.WriteAllText(edited, "{ \"name\": \"mine\" }");
            var before = File.ReadAllBytes(edited);

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.ReadAllBytes(edited).Should().Equal(before);
            console.Output.Should().Contain("already there");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE11. A template-named file without a project file beside it is someone else's work;
    /// the template writer truncates, so this must refuse rather than write.</summary>
    [Fact]
    public void EnsureNoTemplateCollision_WithAStrayTemplateFile_RefusesNamingIt()
    {
        var root = CreateTempRoot();
        try
        {
            var webResources = Path.Combine(root, "WebResources");
            Directory.CreateDirectory(webResources);
            File.WriteAllText(Path.Combine(webResources, "package.json"), "{ \"name\": \"not-ours\" }");

            var act = () => ScaffoldCommand.EnsureNoTemplateCollision(webResources, "WebResources.csproj");

            act.Should().Throw<FlowlineException>()
               .Where(e => e.ExitCode == ExitCode.WriteTargetOccupied)
               .And.Message.Should().Contain("package.json");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The refusal names the interrupted-scaffold recovery, because the guard is what makes a
    /// partially written folder unresumable and there is no flag to overrule it. The folder it names is the
    /// one that was written, not the default — <c>--name</c> can move it.</summary>
    [Fact]
    public void EnsureNoTemplateCollision_Refusal_NamesTheFolderThatWasScaffolded()
    {
        var root = CreateTempRoot();
        try
        {
            var scripts = Path.Combine(root, "Scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "package.json"), "{}");

            var act = () => ScaffoldCommand.EnsureNoTemplateCollision(scripts, "Scripts.csproj");

            act.Should().Throw<FlowlineException>()
               .And.Message.Should().Contain("delete Scripts");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The guard runs before the first write, so a refusal leaves no half-written template behind
    /// and the colliding file is untouched.</summary>
    [Fact]
    public async Task Scaffold_WithAStrayTemplateFile_WritesNothingAndLeavesItIntact()
    {
        var root = CreateTempRoot();
        try
        {
            var webResources = Path.Combine(root, "WebResources");
            Directory.CreateDirectory(webResources);
            var stray = Path.Combine(webResources, "package.json");
            File.WriteAllText(stray, "{ \"name\": \"not-ours\" }");
            var before = File.ReadAllBytes(stray);

            var (command, _) = MakeCommand();
            var act = async () => await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            await act.Should().ThrowAsync<FlowlineException>();
            File.ReadAllBytes(stray).Should().Equal(before);
            File.Exists(Path.Combine(webResources, "WebResources.csproj")).Should().BeFalse();
            File.Exists(Path.Combine(webResources, "tsconfig.json")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- registration -------------------------------------------------------------------------

    const string CdsprojXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string WebResourcesXml = """<Project Sdk="Microsoft.Build.NoTargets/3.7.134"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    /// <summary>Writes a project fixture: a .cdsproj plus a solution file that references it.</summary>
    static async Task<string> CreateProjectFixtureAsync(string root, params (string RelativePath, string Xml)[] extraProjects)
    {
        var cdsproj = Path.Combine("Solution", "Contoso.cdsproj");
        Directory.CreateDirectory(Path.Combine(root, "Solution"));
        File.WriteAllText(Path.Combine(root, cdsproj), CdsprojXml);

        var writer = new MsBuildSolutionWriter();
        var slnPath = Path.Combine(root, "Contoso.slnx");
        await writer.AddProjectAsync(slnPath, cdsproj);

        foreach (var (relativePath, xml) in extraProjects)
        {
            var full = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, xml);
            await writer.AddProjectAsync(slnPath, relativePath);
        }

        return slnPath;
    }

    /// <summary>Covers AE2. A solution file is the whole trigger for naming and registration — no
    /// <c>.flowline</c> anywhere in this fixture, and no Dataverse call in the path.</summary>
    [Fact]
    public async Task Scaffold_WithASolutionFile_NamesTheProjectAfterItAndAddsItToIt()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = await CreateProjectFixtureAsync(root);
            var (command, _) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.Exists(Path.Combine(root, ".flowline")).Should().BeFalse();
            File.Exists(Path.Combine(root, "WebResources", "Contoso.WebResources.csproj")).Should().BeTrue();
            File.ReadAllText(slnPath).Should().Contain("Contoso.WebResources.csproj");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE25. Registration is what makes the project findable, so the run names the solution
    /// file it reached without needing --verbose.</summary>
    [Fact]
    public async Task Scaffold_WithASolutionFile_ReportsWhatItAddedAndWhere()
    {
        var root = CreateTempRoot();
        try
        {
            await CreateProjectFixtureAsync(root);
            var (command, console) = MakeCommand();

            await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            console.Output.Should().Contain("Contoso.WebResources.csproj added to Contoso.slnx");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE14. A named project goes into the solution file under that name.</summary>
    [Fact]
    public async Task Scaffold_WithANameAndASolutionFile_AddsTheNamedProjectToIt()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = await CreateProjectFixtureAsync(root);
            var (command, _) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), "Scripts", CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.Exists(Path.Combine(root, "Scripts", "Scripts.csproj")).Should().BeTrue();
            File.ReadAllText(slnPath).Should().Contain("Scripts.csproj");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE26. <c>--name</c> asks for one specific project, so a default-named
    /// folder sitting beside it is not what "already there" means.</summary>
    [Fact]
    public async Task Scaffold_WithAName_IsNotBlockedByADefaultFolderOutsideTheSolutionFile()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, _) = MakeCommand();
            await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), "Scripts", CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.Exists(Path.Combine(root, "Scripts", "Scripts.csproj")).Should().BeTrue();
            File.Exists(Path.Combine(root, "WebResources", "WebResources.csproj")).Should().BeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE27. Flowline resolves one WebResources project per solution, so a second one in the
    /// solution file would leave one of them silently never pushed. Refused before anything is written.</summary>
    [Fact]
    public async Task Scaffold_WithANameWhenTheSolutionAlreadyHasAWebResourcesProject_Refuses()
    {
        var root = CreateTempRoot();
        try
        {
            await CreateProjectFixtureAsync(root,
                (Path.Combine("WebResources", "Contoso.WebResources.csproj"), WebResourcesXml));
            var (command, _) = MakeCommand();

            var act = async () => await command.ScaffoldWebResourcesAsync(Target(root), "Scripts", CancellationToken.None);

            // Same code as the other --name refusal: both are "this name won't work here".
            (await act.Should().ThrowAsync<FlowlineException>())
                .Where(e => e.ExitCode == ExitCode.ValidationFailed)
                .And.Message.Should().Contain("one per solution");
            Directory.Exists(Path.Combine(root, "Scripts")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A second run of the same named scaffold is a reporting no-op, not a refusal.</summary>
    [Fact]
    public async Task Scaffold_WithAName_RunAgain_SkipsNamingTheProjectItFound()
    {
        var root = CreateTempRoot();
        try
        {
            await CreateProjectFixtureAsync(root);
            var (command, console) = MakeCommand();
            await command.ScaffoldWebResourcesAsync(Target(root), "Scripts", CancellationToken.None);

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), "Scripts", CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            console.Output.Should().Contain("Scripts.csproj already there");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A second run reports already-there and does not write a duplicate entry.</summary>
    [Fact]
    public async Task Scaffold_WithASolutionFile_RunAgain_SkipsWithoutDuplicatingTheSolutionEntry()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = await CreateProjectFixtureAsync(root);
            var (command, console) = MakeCommand();
            await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            console.Output.Should().Contain("already there");
            var entries = File.ReadAllText(slnPath).Split("Contoso.WebResources.csproj").Length - 1;
            entries.Should().Be(1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A WebResources project the solution file records somewhere other than the default folder is
    /// still detected, so no second copy is scaffolded beside it.</summary>
    [Fact]
    public async Task Scaffold_WithAMovedWebResourcesProject_DoesNotScaffoldADuplicate()
    {
        var root = CreateTempRoot();
        try
        {
            await CreateProjectFixtureAsync(root,
                (Path.Combine("src", "Web", "Contoso.WebResources.csproj"), WebResourcesXml));
            var (command, console) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            console.Output.Should().Contain("already there");
            Directory.Exists(Path.Combine(root, "WebResources")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE22, AE28. The project is written where the user is standing and its entry is added
    /// to the solution file found above it — the two do not have to sit in the same folder.</summary>
    [Fact]
    public async Task Scaffold_FromASubfolder_WritesLocallyAndAddsItToTheSolutionFileAbove()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = await CreateProjectFixtureAsync(root);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var nested = Path.Combine(root, "Plugins");
            Directory.CreateDirectory(nested);
            var (command, _) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(nested), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.Exists(Path.Combine(nested, "WebResources", "Contoso.WebResources.csproj")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, "WebResources")).Should().BeFalse();
            File.ReadAllText(slnPath).Should().Contain("Plugins");
            File.ReadAllText(slnPath).Should().Contain("Contoso.WebResources.csproj");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE29. One WebResources project per Flowline project: a run from a subfolder still
    /// sees the one the solution file already records, so no second is written.</summary>
    [Fact]
    public async Task Scaffold_FromASubfolder_WhenTheSolutionFileAlreadyHasOne_Skips()
    {
        var root = CreateTempRoot();
        try
        {
            await CreateProjectFixtureAsync(root,
                (Path.Combine("WebResources", "Contoso.WebResources.csproj"), WebResourcesXml));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var nested = Path.Combine(root, "Plugins");
            Directory.CreateDirectory(nested);
            var (command, console) = MakeCommand();

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(nested), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            console.Output.Should().Contain("already there");
            Directory.Exists(Path.Combine(nested, "WebResources")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE18. A folder scaffolded before the repo had a solution file holds a generically
    /// named project that is in no solution file. It is left alone, and the report says what it is rather
    /// than a dim skip that reads as handled.</summary>
    [Fact]
    public async Task Scaffold_OverAGenericallyNamedProject_NamesWhatIsThereAndLeavesItAlone()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, console) = MakeCommand();
            await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);
            await CreateProjectFixtureAsync(root);

            var exitCode = await command.ScaffoldWebResourcesAsync(Target(root), name: null, CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            console.Output.Should().Contain("Contoso.WebResources.csproj");
            File.Exists(Path.Combine(root, "WebResources", "WebResources.csproj")).Should().BeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
