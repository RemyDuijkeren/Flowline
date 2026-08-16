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
/// Covers the two decisions <c>flowline scaffold</c> makes before it writes anything: whether the part is
/// one it can write, and which mode the folder gets. Both are static and side-effect free, so every branch
/// is reachable without constructing the command or a console.
/// </summary>
public class ScaffoldCommandTests
{
    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void WriteFlowlineConfig(string folder) =>
        File.WriteAllText(Path.Combine(folder, ".flowline"), "{}");

    static void WriteSolutionFile(string folder) =>
        File.WriteAllText(Path.Combine(folder, "TestSolution.slnx"), "<Solution />");

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

    [Fact]
    public void ResolveTarget_WithNeitherMarker_IsStandalone()
    {
        var root = CreateTempRoot();
        try
        {
            var target = ScaffoldCommand.ResolveTarget(root);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Standalone);
            target.Folder.Should().Be(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveTarget_WithBothMarkers_IsProjectMode()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFlowlineConfig(root);
            WriteSolutionFile(root);

            var target = ScaffoldCommand.ResolveTarget(root);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Project);
            target.Folder.Should().Be(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE9. A solution file alone does not make a Flowline project — <c>.flowline</c> does,
    /// and it is the marker every other command walks upward to find.</summary>
    [Fact]
    public void ResolveTarget_WithASolutionFileButNoConfig_IsStandalone()
    {
        var root = CreateTempRoot();
        try
        {
            WriteSolutionFile(root);

            var target = ScaffoldCommand.ResolveTarget(root);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Standalone);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE8. Half a project fails rather than quietly writing a generically-named project
    /// into a repo that expected a solution-named one.</summary>
    [Fact]
    public void ResolveTarget_WithAConfigButNoSolutionFile_FailsNamingTheMissingSolutionFile()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFlowlineConfig(root);

            var act = () => ScaffoldCommand.ResolveTarget(root);

            act.Should().Throw<FlowlineException>()
               .Where(e => e.ExitCode == ExitCode.NotFound)
               .And.Message.Should().Contain(".slnx");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE6. The project marker resolves upward, so a run from inside a project lands in
    /// project mode and writes into the project root — not beside the caller.</summary>
    [Fact]
    public void ResolveTarget_FromASubdirectoryOfAProject_ResolvesToTheProjectRoot()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFlowlineConfig(root);
            WriteSolutionFile(root);
            var nested = Path.Combine(root, "Plugins", "Handlers");
            Directory.CreateDirectory(nested);

            var target = ScaffoldCommand.ResolveTarget(nested);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Project);
            target.Folder.Should().Be(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE1. Standalone writes the template under a generic project name and touches
    /// nothing else — no solution file, no config.</summary>
    [Fact]
    public async Task ScaffoldStandalone_WritesTheTemplateUnderTheGenericProjectName()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, _) = MakeCommand();

            var exitCode = await command.ScaffoldStandaloneAsync(root, CancellationToken.None);

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

    /// <summary>Covers AE3. A second run is a reporting no-op, even when the user has edited the template.</summary>
    [Fact]
    public async Task ScaffoldStandalone_RunAgainOverAnEditedTemplate_SkipsAndChangesNothing()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, console) = MakeCommand();
            await command.ScaffoldStandaloneAsync(root, CancellationToken.None);

            var edited = Path.Combine(root, "WebResources", "package.json");
            File.WriteAllText(edited, "{ \"name\": \"mine\" }");
            var before = File.ReadAllBytes(edited);

            var exitCode = await command.ScaffoldStandaloneAsync(root, CancellationToken.None);

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
    /// partially written folder unresumable and there is no flag to overrule it.</summary>
    [Fact]
    public void EnsureNoTemplateCollision_Refusal_NamesTheInterruptedScaffoldRecovery()
    {
        var root = CreateTempRoot();
        try
        {
            var webResources = Path.Combine(root, "WebResources");
            Directory.CreateDirectory(webResources);
            File.WriteAllText(Path.Combine(webResources, "package.json"), "{}");

            var act = () => ScaffoldCommand.EnsureNoTemplateCollision(webResources, "WebResources.csproj");

            act.Should().Throw<FlowlineException>()
               .And.Message.Should().Contain("delete WebResources");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The guard runs before the first write, so a refusal leaves no half-written template behind
    /// and the colliding file is untouched.</summary>
    [Fact]
    public async Task ScaffoldStandalone_WithAStrayTemplateFile_WritesNothingAndLeavesItIntact()
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
            var act = async () => await command.ScaffoldStandaloneAsync(root, CancellationToken.None);

            await act.Should().ThrowAsync<FlowlineException>();
            File.ReadAllBytes(stray).Should().Equal(before);
            File.Exists(Path.Combine(webResources, "WebResources.csproj")).Should().BeFalse();
            File.Exists(Path.Combine(webResources, "tsconfig.json")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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

    /// <summary>Covers AE2. Project mode names the project after the configured solution and registers it,
    /// with no Dataverse call anywhere in the path.</summary>
    [Fact]
    public async Task ScaffoldIntoProject_NamesTheProjectAfterTheSolutionAndRegistersIt()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = await CreateProjectFixtureAsync(root);
            var (command, _) = MakeCommand();

            var exitCode = await command.ScaffoldIntoProjectAsync(root, "Contoso", CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            File.Exists(Path.Combine(root, "WebResources", "Contoso.WebResources.csproj")).Should().BeTrue();
            File.ReadAllText(slnPath).Should().Contain("Contoso.WebResources.csproj");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A second project-mode run reports already-there and does not register a duplicate entry.</summary>
    [Fact]
    public async Task ScaffoldIntoProject_RunAgain_SkipsWithoutDuplicatingTheSolutionEntry()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = await CreateProjectFixtureAsync(root);
            var (command, console) = MakeCommand();
            await command.ScaffoldIntoProjectAsync(root, "Contoso", CancellationToken.None);

            var exitCode = await command.ScaffoldIntoProjectAsync(root, "Contoso", CancellationToken.None);

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
    public async Task ScaffoldIntoProject_WithAMovedWebResourcesProject_DoesNotScaffoldADuplicate()
    {
        var root = CreateTempRoot();
        try
        {
            await CreateProjectFixtureAsync(root,
                (Path.Combine("src", "Web", "Contoso.WebResources.csproj"), WebResourcesXml));
            var (command, console) = MakeCommand();

            var exitCode = await command.ScaffoldIntoProjectAsync(root, "Contoso", CancellationToken.None);

            exitCode.Should().Be((int)ExitCode.Success);
            console.Output.Should().Contain("already there");
            Directory.Exists(Path.Combine(root, "WebResources")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The project file is named after the solution, so a project with no configured solution name
    /// fails naming the repair rather than writing a generically-named project into it.</summary>
    [Fact]
    public async Task ScaffoldIntoProject_WithNoConfiguredSolutionName_Fails()
    {
        var root = CreateTempRoot();
        try
        {
            await CreateProjectFixtureAsync(root);
            var (command, _) = MakeCommand();

            var act = async () => await command.ScaffoldIntoProjectAsync(root, null, CancellationToken.None);

            (await act.Should().ThrowAsync<FlowlineException>())
                .Where(e => e.ExitCode == ExitCode.ConfigInvalid);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE7. The push step needs a solution and an auth profile this folder cannot invent, so
    /// the next-step block names both rather than implying push runs bare.</summary>
    [Fact]
    public async Task ScaffoldStandalone_NextSteps_NameTheBuildPushAuthenticationAndSolution()
    {
        var root = CreateTempRoot();
        try
        {
            var (command, console) = MakeCommand();

            await command.ScaffoldStandaloneAsync(root, CancellationToken.None);

            console.Output.Should().Contain("npm run build");
            console.Output.Should().Contain("pac auth create");
            console.Output.Should().Contain("flowline push");
            console.Output.Should().Contain("<solution>");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
