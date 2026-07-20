using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Models;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Spectre.Console.Cli;

namespace Flowline.Tests;

public class PushCommandTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public PushCommandTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void IsStandaloneMode_WithPluginFile_ShouldReturnTrue()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll" };

        PushCommand.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void IsStandaloneMode_WithWebResources_ShouldReturnTrue()
    {
        var settings = new PushCommand.Settings { WebResources = "dist" };

        PushCommand.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void ResolveScope_ProjectMode_WithoutScope_ShouldDefaultToAll()
    {
        var settings = new PushCommand.Settings();

        PushCommand.ResolveScope(settings, standaloneMode: false).Should().Be(PushCommand.PushScope.All);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginFileAndWebResources_ShouldDeriveScope()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll", WebResources = "dist" };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.Plugins | PushCommand.PushScope.WebResources);
    }

    [Fact]
    public void ResolveScope_WithAssemblyOnly_ShouldReturnAssemblyOnly()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.AssemblyOnly] };

        PushCommand.ResolveScope(settings, standaloneMode: false).Should().Be(PushCommand.PushScope.AssemblyOnly);
    }

    [Fact]
    public void ResolveScope_WithAssemblyOnlyAndPlugins_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.AssemblyOnly, PushCommand.PushScope.Plugins] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: false);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginFileAndAssemblyOnlyScope_ShouldReturnAssemblyOnly()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll", Scopes = [PushCommand.PushScope.AssemblyOnly] };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.AssemblyOnly);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginsScopeAndPluginFile_ShouldReturnPlugins()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll", Scopes = [PushCommand.PushScope.Plugins] };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.Plugins);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginsScopeButNoPluginFile_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.Plugins] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: true);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithWebResourcesScopeButNoPath_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.WebResources] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: true);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_WithFormEvents_ShouldReturnFormEvents()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.FormEvents] };

        PushCommand.ResolveScope(settings, standaloneMode: false).Should().Be(PushCommand.PushScope.FormEvents);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithFormEventsScopeButNoWebResourcesPath_ShouldThrow()
    {
        // FormEvents reads its annotations from the same --webresources folder web resource sync uses,
        // so it needs that path even when requested on its own, without --scope webresources.
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.FormEvents] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: true);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithFormEventsScopeAndWebResourcesPath_ShouldReturnFormEvents()
    {
        var settings = new PushCommand.Settings { WebResources = "dist", Scopes = [PushCommand.PushScope.FormEvents] };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.FormEvents);
    }

    [Fact]
    public void ValidateStandaloneMode_WithFlowlineFile_ShouldThrow()
    {
        File.WriteAllText(Path.Combine(_root, ".flowline"), "{}");
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll" };

        var act = () => PushCommand.ValidateStandaloneMode(settings, _root);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveStandaloneSolutionName_WithoutSolution_ShouldThrow()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll" };

        var act = () => PushCommand.ResolveStandaloneSolutionName(settings);

        act.Should().Throw<FlowlineException>();
    }

    // -- R8: project mode's positional [solution] validated against the single configured solution --

    [Fact]
    public void ValidateSolutionMatchesConfig_NoInputName_DoesNotThrow()
    {
        var act = () => PushCommand.ValidateSolutionMatchesConfig(null, "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionMatchesConfig_MatchingNameCaseInsensitive_DoesNotThrow()
    {
        var act = () => PushCommand.ValidateSolutionMatchesConfig("contosocustomizations", "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionMatchesConfig_MatchingNameWithWhitespace_DoesNotThrow()
    {
        var act = () => PushCommand.ValidateSolutionMatchesConfig("  ContosoCustomizations  ", "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionMatchesConfig_MismatchedName_Throws()
    {
        var act = () => PushCommand.ValidateSolutionMatchesConfig("OtherSolution", "ContosoCustomizations");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                && e.Message.Contains("OtherSolution") && e.Message.Contains("ContosoCustomizations"));
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithExistingDll_ShouldReturnFullPath()
    {
        var dll = Path.Combine(_root, "plugins.dll");
        File.WriteAllText(dll, "");
        var settings = new PushCommand.Settings { PluginFile = dll };

        PushCommand.ResolveStandalonePluginFilePath(settings).Should().Be(Path.GetFullPath(dll));
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithNonDll_ShouldThrow()
    {
        var file = Path.Combine(_root, "plugins.txt");
        File.WriteAllText(file, "");
        var settings = new PushCommand.Settings { PluginFile = file };

        var act = () => PushCommand.ResolveStandalonePluginFilePath(settings);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithNupkg_ShouldReturnFullPath()
    {
        // R2a/KD6: .nupkg is now accepted, routing to the same package entry point as project mode,
        // rather than the old "NuGet packages not yet supported" rejection.
        var file = Path.Combine(_root, "plugins.nupkg");
        File.WriteAllText(file, "");
        var settings = new PushCommand.Settings { PluginFile = file };

        PushCommand.ResolveStandalonePluginFilePath(settings).Should().Be(Path.GetFullPath(file));
    }

    // -- ResolvePluginPushPath (R1, KD1) --
    // Layout mirrors a real `dotnet build --configuration Release` of a `pac plugin init`-scaffolded
    // project (verified locally): the .dll lands at <buildOutputRoot>/net462/publish/Plugins.dll, while
    // `dotnet pack`'s .nupkg lands directly at <buildOutputRoot>/Plugins.1.0.0.nupkg — a sibling of
    // net462/, not alongside the .dll itself. ResolvePluginPushPath must search the whole root.

    [Fact]
    public void ResolvePluginPushPath_DllAndNupkgAutoMode_ShouldReturnNupkg()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        var nupkg = Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg");
        File.WriteAllText(dll, "");
        File.WriteAllText(nupkg, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto).Should().Be(nupkg);
    }

    [Fact]
    public void ResolvePluginPushPath_DllAndNupkgDllMode_ShouldReturnDll()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        var nupkg = Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg");
        File.WriteAllText(dll, "");
        File.WriteAllText(nupkg, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Dll).Should().Be(dll);
    }

    [Fact]
    public void ResolvePluginPushPath_DllAndNupkgNupkgMode_ShouldReturnNupkg()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        var nupkg = Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg");
        File.WriteAllText(dll, "");
        File.WriteAllText(nupkg, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Nupkg).Should().Be(nupkg);
    }

    [Fact]
    public void ResolvePluginPushPath_DllOnlyAutoMode_ShouldReturnDll()
    {
        // Regression guard: most existing plugin projects don't produce a .nupkg yet.
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto).Should().Be(dll);
    }

    [Fact]
    public void ResolvePluginPushPath_DllOnlyNupkgMode_ThrowsInsteadOfSilentlyFallingBackToDll()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, "");

        var act = () => PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Nupkg);

        act.Should().Throw<Flowline.Core.FlowlineException>().WithMessage("*Nupkg*no .nupkg was found*");
    }

    [Fact]
    public void ResolvePluginPushPath_MissingBuildOutputRootAutoMode_ShouldReturnDll()
    {
        // --no-build with a stale/missing bin folder shouldn't throw here — the earlier File.Exists(dll)
        // check in PreparePluginsForPushAsync already guards the real "nothing built" case.
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var dll = Path.Combine(_root, "Plugins.dll");
        File.WriteAllText(dll, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto).Should().Be(dll);
    }

    [Fact]
    public void ResolvePluginPushPath_TwoNupkgVersionsPresent_ThrowsInsteadOfPickingArbitrarily()
    {
        // Regression guard: a version bump without a clean build leaves the old versioned .nupkg
        // sitting alongside the new one — directory enumeration order is unspecified, so silently
        // picking one (the prior FirstOrDefault() behavior) could push stale content.
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, "");
        File.WriteAllText(Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg"), "");
        File.WriteAllText(Path.Combine(buildOutputRoot, "Plugins.1.0.1.nupkg"), "");

        var act = () => PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto);

        act.Should().Throw<Flowline.Core.FlowlineException>()
            .WithMessage("*Plugins.1.0.0.nupkg*Plugins.1.0.1.nupkg*");
    }

    // -- U4: every confirmed project pushes in one invocation (R5, R6, KD4) --

    static PushCommand.PluginPushTarget Target(string projectName, string assemblyName, params string[] extraPackageAssemblies) =>
        new($@"C:\{projectName}\bin\Release\{assemblyName}.dll",
            assemblyName,
            projectName,
            extraPackageAssemblies.Length == 0
                ? null
                : extraPackageAssemblies.Prepend(assemblyName)
                                        .Select(n => new PluginAssemblyMetadata(n, "1.0.0.0", [], "", "", null, "", []))
                                        .ToList());

    // KD4/R6: PluginPackageMode is one per-solution setting, so the same mode meets whatever each
    // project's own build output happens to look like. AE5 is the mixed case — Sales packs a .nupkg,
    // Support never enabled packaging — and under Auto each still resolves to its own real artifact.

    [Fact]
    public void ResolvePluginPushPath_TwoProjectsOneNupkgOneDllAutoMode_ShouldResolveEachToItsOwnArtifact()
    {
        var (salesRoot, salesDll) = WriteProjectOutput("Sales", "Plugins.Sales", withNupkg: true);
        var (supportRoot, supportDll) = WriteProjectOutput("Support", "Plugins.Support", withNupkg: false);

        PushCommand.ResolvePluginPushPath(salesDll, salesRoot, PluginPackageMode.Auto)
                   .Should().Be(Path.Combine(salesRoot, "Plugins.Sales.1.0.0.nupkg"));
        PushCommand.ResolvePluginPushPath(supportDll, supportRoot, PluginPackageMode.Auto)
                   .Should().Be(supportDll);
    }

    [Fact]
    public void ResolvePluginPushPath_TwoProjectsOneNupkgOneDllDllMode_ShouldResolveBothToTheirDll()
    {
        // The solution declared Dll, so the project that does produce a .nupkg follows the solution
        // rather than its own state — the whole point of KD4's single per-solution setting.
        var (salesRoot, salesDll) = WriteProjectOutput("Sales", "Plugins.Sales", withNupkg: true);
        var (supportRoot, supportDll) = WriteProjectOutput("Support", "Plugins.Support", withNupkg: false);

        PushCommand.ResolvePluginPushPath(salesDll, salesRoot, PluginPackageMode.Dll).Should().Be(salesDll);
        PushCommand.ResolvePluginPushPath(supportDll, supportRoot, PluginPackageMode.Dll).Should().Be(supportDll);
    }

    [Fact]
    public void ResolvePluginPushPath_TwoProjectsOneNupkgOneDllNupkgMode_ShouldThrowNamingTheProjectWithoutOne()
    {
        var (salesRoot, salesDll) = WriteProjectOutput("Sales", "Plugins.Sales", withNupkg: true);
        var (supportRoot, supportDll) = WriteProjectOutput("Support", "Plugins.Support", withNupkg: false);

        PushCommand.ResolvePluginPushPath(salesDll, salesRoot, PluginPackageMode.Nupkg)
                   .Should().Be(Path.Combine(salesRoot, "Plugins.Sales.1.0.0.nupkg"));

        var act = () => PushCommand.ResolvePluginPushPath(supportDll, supportRoot, PluginPackageMode.Nupkg);
        act.Should().Throw<Flowline.Core.FlowlineException>().WithMessage("*no .nupkg was found*");
    }

    (string BuildOutputRoot, string Dll) WriteProjectOutput(string projectFolder, string assemblyName, bool withNupkg)
    {
        var buildOutputRoot = Path.Combine(_root, projectFolder, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);

        var dll = Path.Combine(publishDir, $"{assemblyName}.dll");
        File.WriteAllText(dll, "");
        if (withNupkg) File.WriteAllText(Path.Combine(buildOutputRoot, $"{assemblyName}.1.0.0.nupkg"), "");

        return (buildOutputRoot, dll);
    }

    // -- CollectPushedAssemblyNames: the set every project's orphan sweep must spare --

    [Fact]
    public void CollectPushedAssemblyNames_WithTwoProjects_ShouldIncludeBothAssemblies()
    {
        PushCommand.CollectPushedAssemblyNames([Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")])
                   .Should().BeEquivalentTo(["Sales", "Support"]);
    }

    [Fact]
    public void CollectPushedAssemblyNames_WithSingleProject_ShouldIncludeOnlyThatAssembly()
    {
        PushCommand.CollectPushedAssemblyNames([Target("Plugins", "Plugins")]).Should().BeEquivalentTo(["Plugins"]);
    }

    [Fact]
    public void CollectPushedAssemblyNames_WithMultiAssemblyPackage_ShouldIncludeItsNonPrimaryAssemblies()
    {
        // A .nupkg project alongside a classic one: the classic project's orphan sweep is solution-wide,
        // so it needs the package's other assemblies by name or it reads them as having no local source.
        PushCommand.CollectPushedAssemblyNames(
        [
            Target("Plugins.Sales", "Sales", "Sales.Shared", "Sales.Workflows"),
            Target("Plugins.Support", "Support"),
        ]).Should().BeEquivalentTo(["Sales", "Sales.Shared", "Sales.Workflows", "Support"]);
    }

    [Fact]
    public void CollectPushedAssemblyNames_WithTheSameAssemblyNameTwice_ShouldDedupeIgnoringCase()
    {
        PushCommand.CollectPushedAssemblyNames([Target("Plugins.A", "Shared"), Target("Plugins.B", "SHARED")])
                   .Should().ContainSingle();
    }

    // -- Per-project output: attributable at N=2, untouched at N=1 (R7) --

    [Fact]
    public void DescribePluginPushHeader_WithSingleProject_ShouldReturnNull()
    {
        // R7 regression guard: a one-project solution must print exactly what it printed before U4.
        PushCommand.DescribePluginPushHeader([Target("Plugins", "Plugins")], 0).Should().BeNull();
    }

    [Fact]
    public void DescribePluginPushHeader_WithTwoProjects_ShouldNameEachProject()
    {
        PushCommand.PluginPushTarget[] targets = [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")];

        PushCommand.DescribePluginPushHeader(targets, 0).Should().Be("[bold]Plugins.Sales[/] — pushing");
        PushCommand.DescribePluginPushHeader(targets, 1).Should().Be("[bold]Plugins.Support[/] — pushing");
    }

    // -- Per-project failure: names which one, and what the org holds now --

    [Fact]
    public void DescribePluginPushFailure_WithSingleProject_ShouldReturnNull()
    {
        // R7: nothing to attribute, so the original exception reaches the user unwrapped.
        PushCommand.DescribePluginPushFailure([Target("Plugins", "Plugins")], 0, "Build failed.").Should().BeNull();
    }

    [Fact]
    public void DescribePluginPushFailure_WithSecondOfTwoFailing_ShouldNameItAndWhatAlreadyLanded()
    {
        PushCommand.PluginPushTarget[] targets = [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")];

        var message = PushCommand.DescribePluginPushFailure(targets, 1, "Assembly registration failed.");

        message.Should().Be("'Plugins.Support' failed to push: Assembly registration failed. " +
                            "Already in the org: Plugins.Sales. Fix 'Plugins.Support', then push again.");
    }

    [Fact]
    public void DescribePluginPushFailure_WithFirstOfThreeFailing_ShouldReportTheOnesNeverAttempted()
    {
        // The others aren't skipped silently — the message says they weren't tried, which is a different
        // state from "pushed" and the user has to be able to tell them apart.
        PushCommand.PluginPushTarget[] targets =
            [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support"), Target("Plugins.Ops", "Ops")];

        var message = PushCommand.DescribePluginPushFailure(targets, 0, "Assembly registration failed.");

        message.Should().Be("'Plugins.Sales' failed to push: Assembly registration failed. " +
                            "Not attempted: Plugins.Support, Plugins.Ops. Fix 'Plugins.Sales', then push again.");
    }

    [Fact]
    public void DescribePluginPushFailure_WithMiddleOfThreeFailing_ShouldReportBothPushedAndNotAttempted()
    {
        PushCommand.PluginPushTarget[] targets =
            [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support"), Target("Plugins.Ops", "Ops")];

        PushCommand.DescribePluginPushFailure(targets, 1, "Assembly registration failed.")
                   .Should().Contain("Already in the org: Plugins.Sales.")
                   .And.Contain("Not attempted: Plugins.Ops.");
    }

    [Fact]
    public void DescribePluginPushFailure_WithUnpunctuatedReason_ShouldCloseTheSentence()
    {
        PushCommand.PluginPushTarget[] targets = [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")];

        PushCommand.DescribePluginPushFailure(targets, 0, "Connection reset by peer")
                   .Should().Contain("Connection reset by peer. Not attempted:");
    }

    // -- IsPackagePush (KD6 — shared routing decision for project mode and standalone) --

    [Fact]
    public void IsPackagePush_WithNupkgPath_ShouldReturnTrue()
    {
        PushCommand.IsPackagePush(Path.Combine(_root, "Plugins.nupkg")).Should().BeTrue();
    }

    [Fact]
    public void IsPackagePush_WithDllPath_ShouldReturnFalse()
    {
        PushCommand.IsPackagePush(Path.Combine(_root, "Plugins.dll")).Should().BeFalse();
    }

    // -- ResolveStandalonePackageAssemblyName (R2a — standalone mode has no project context) --
    // A real .nupkg filename typically embeds its NuGet version (e.g. "MyPlugins.1.0.0.nupkg"), which
    // does not match the reflected assembly name inside it ("MyPlugins") — Path.GetFileNameWithoutExtension
    // would return "MyPlugins.1.0.0", not the real assembly name SyncSolutionFromPackageAsync needs to
    // match its primary assembly. These tests build a real minimal plugin DLL and pack it under a
    // deliberately version-suffixed nupkg filename to prove the fix resolves the real name instead.

    [Fact]
    public void ResolveStandalonePackageAssemblyName_VersionedNupkgFilename_ReturnsRealAssemblyNameNotFilename()
    {
        CopyXrmSdkDllNextTo(_root);
        var dllPath = BuildPluginDll(_root, "MyPlugins", "MyPlugins.SomePlugin");
        var nupkgPath = Path.Combine(_root, "MyPlugins.1.0.0.nupkg"); // filename != assembly name
        BuildNupkg(nupkgPath, dllPath);

        var (name, assemblies) = PushCommand.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

        name.Should().Be("MyPlugins");
        name.Should().NotBe("MyPlugins.1.0.0");
        assemblies.Should().ContainSingle(a => a.Name == "MyPlugins");
    }

    [Fact]
    public void ResolveStandalonePackageAssemblyName_MultiplePluginBearingAssemblies_ThrowsActionableError()
    {
        CopyXrmSdkDllNextTo(_root);
        var dllA = BuildPluginDll(_root, "AssemblyA", "AssemblyA.PluginA");
        var dllB = BuildPluginDll(_root, "AssemblyB", "AssemblyB.PluginB");
        var nupkgPath = Path.Combine(_root, "Multi.1.0.0.nupkg");
        BuildNupkg(nupkgPath, dllA, dllB);

        var act = () => PushCommand.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

        act.Should().Throw<Flowline.Core.FlowlineException>()
            .WithMessage("*AssemblyA*AssemblyB*");
    }

    [Fact]
    public void ResolveStandalonePackageAssemblyName_NoPluginBearingAssemblies_FallsBackToFilename()
    {
        // R3a fires inside SyncSolutionFromPackageAsync regardless of what name is returned here — this
        // fallback is never actually consumed to resolve a "primary" assembly in that case.
        var dependencyDll = BuildDependencyDll(_root, "JustADependency", "SomeNamespace.SomeType");
        var nupkgPath = Path.Combine(_root, "Empty.1.0.0.nupkg");
        BuildNupkg(nupkgPath, dependencyDll);

        var (name, assemblies) = PushCommand.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

        name.Should().Be("Empty.1.0.0");
        assemblies.Should().BeEmpty();
    }

    // Builds a minimal real assembly on disk with one public class implementing IPlugin — mirrors
    // PluginAssemblyReaderTests' BuildPluginDll (Flowline.Core.Tests), duplicated here since test
    // projects don't share fixture code across assemblies.
    private static string BuildPluginDll(string dir, string assemblyName, string pluginTypeName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");

        var pluginTb = mb.DefineType(pluginTypeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object), [typeof(IPlugin)]);
        var executeMethod = typeof(IPlugin).GetMethod(nameof(IPlugin.Execute))!;
        var methodBuilder = pluginTb.DefineMethod(nameof(IPlugin.Execute),
            MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), [typeof(IServiceProvider)]);
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
        pluginTb.DefineMethodOverride(methodBuilder, executeMethod);
        pluginTb.CreateType();

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // Builds a minimal real assembly with one public class that does NOT implement IPlugin — a
    // stand-in for a pure-dependency DLL.
    private static string BuildDependencyDll(string dir, string assemblyName, string typeName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");
        mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // Zips the given DLLs into a .nupkg under lib/net462/ at the given path (caller picks the filename
    // deliberately, to test version-suffixed-filename-vs-assembly-name mismatches).
    private static void BuildNupkg(string nupkgPath, params string[] dllPaths)
    {
        using var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);
        foreach (var dllPath in dllPaths)
            archive.CreateEntryFromFile(dllPath, $"lib/net462/{Path.GetFileName(dllPath)}");
    }

    // Copies Microsoft.Xrm.Sdk.dll next to the .nupkg (not inside its lib/ folder) — mirrors a real
    // pac-plugin-init package, where the SDK assembly is copy-local to the build output but excluded
    // from the packed nupkg content (PrivateAssets="All"). AnalyzePackage's resolver widening to the
    // nupkg's own directory is what makes a real IPlugin-referencing test DLL resolvable without this.
    private static void CopyXrmSdkDllNextTo(string dir) =>
        File.Copy(
            Path.Combine(Path.GetDirectoryName(typeof(PushCommandTests).Assembly.Location)!, "Microsoft.Xrm.Sdk.dll"),
            Path.Combine(dir, "Microsoft.Xrm.Sdk.dll"),
            overwrite: true);

    [Fact]
    public void ResolveStandaloneWebResourcesPath_WithExistingFolder_ShouldReturnFullPath()
    {
        var folder = Path.Combine(_root, "dist");
        Directory.CreateDirectory(folder);
        var settings = new PushCommand.Settings { WebResources = folder };

        PushCommand.ResolveStandaloneWebResourcesPath(settings).Should().Be(Path.GetFullPath(folder));
    }

    [Fact]
    public void EnsureBuiltWebResources_WithFiles_ShouldNotThrow()
    {
        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);
        File.WriteAllText(Path.Combine(dist, "account.js"), "");

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithNestedFiles_ShouldNotThrow()
    {
        var dist = Path.Combine(_root, "dist");
        var sub = Path.Combine(dist, "forms");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "form.js"), "");

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithEmptyFolder_ShouldThrow()
    {
        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithMissingFolder_ShouldThrow()
    {
        var dist = Path.Combine(_root, "dist");

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().Throw<FlowlineException>();
    }

    // -- ValidateForce (R5, R11, AE1a) --

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingValidValues()
    {
        var settings = new PushCommand.Settings { Force = ["banana"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, PushCommand.ValidSpecifiers, "push");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                && e.Message.Contains("delete-orphans") && e.Message.Contains("recreate-assembly")
                && e.Message.Contains("delete-form-handlers") && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_SyncOnlyValue_ThrowsNamingPushValidValues()
    {
        var settings = new PushCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, PushCommand.ValidSpecifiers, "push");

        act.Should().Throw<FlowlineException>().Where(e => e.Message.Contains("dirty") && e.Message.Contains("push"));
    }

    [Fact]
    public void ValidateForce_ValidValues_DoesNotThrow()
    {
        var settings = new PushCommand.Settings { Force = ["delete-orphans", "config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, PushCommand.ValidSpecifiers, "push");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateForce_All_DoesNotThrow()
    {
        var settings = new PushCommand.Settings { Force = ["all"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, PushCommand.ValidSpecifiers, "push");

        act.Should().NotThrow();
    }

    // -- HasForce wiring (R6) --

    [Fact]
    public void HasForce_DeleteOrphansOnly_DoesNotApproveRecreateAssembly()
    {
        var settings = new PushCommand.Settings { Force = ["delete-orphans"] };

        settings.HasForce("delete-orphans").Should().BeTrue();
        settings.HasForce("recreate-assembly").Should().BeFalse();
        settings.HasForce("delete-form-handlers").Should().BeFalse();
    }

    [Fact]
    public void HasForce_All_ApprovesEveryPushHazard()
    {
        var settings = new PushCommand.Settings { Force = ["all"] };

        settings.HasForce("delete-orphans").Should().BeTrue();
        settings.HasForce("recreate-assembly").Should().BeTrue();
        settings.HasForce("delete-form-handlers").Should().BeTrue();
        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void HasForce_RepeatedFlag_ApprovesExactlyThoseTwo()
    {
        var settings = new PushCommand.Settings { Force = ["delete-orphans", "config"] };

        settings.HasForce("delete-orphans").Should().BeTrue();
        settings.HasForce("config").Should().BeTrue();
        settings.HasForce("recreate-assembly").Should().BeFalse();
        settings.HasForce("delete-form-handlers").Should().BeFalse();
    }

    // -- CommandApp parse seam (R1/R2/R3, AE1) — exit-code-only per KTD3's test-seam split;
    // Spectre renders parse errors via AnsiConsole, not Console.Out, so message content isn't
    // capturable via simple redirection (confirmed during planning). A no-op wrapper command
    // drives Spectre's real parser against the real PushCommand.Settings shape without needing
    // PushCommand's full DI graph, which parsing itself never touches.

    sealed class NoOpPushSettingsCommand : Command<PushCommand.Settings>
    {
        protected override int Execute(CommandContext context, PushCommand.Settings settings, CancellationToken cancellationToken) => 0;
    }

    static CommandApp BuildPushParseProbe()
    {
        var app = new CommandApp();
        app.Configure(c => c.AddCommand<NoOpPushSettingsCommand>("push"));
        return app;
    }

    [Fact]
    public void CommandApp_BareForce_NoValue_FailsToParse()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "--force"]);

        exitCode.Should().NotBe(0);
    }

    [Fact]
    public void CommandApp_ForceWithValue_ParsesSuccessfully()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "--force", "delete-orphans"]);

        exitCode.Should().Be(0);
    }

    [Fact]
    public void CommandApp_ForceRepeated_CollectsBothValues()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "--force", "delete-orphans", "--force", "config"]);

        exitCode.Should().Be(0);
    }

    // -- Positional-argument interaction (behavior-change note) --

    [Fact]
    public void CommandApp_ForceImmediatelyFollowedByPositional_BindsPositionalAsForceValue()
    {
        // "--force MySolution" with the [solution] argument otherwise omitted binds "MySolution"
        // as --force's value and leaves the positional unset — not a silent misfire, since a
        // real invocation would still fail ValidateForce naming push's valid values.
        PushCommand.Settings? captured = null;
        var app = new CommandApp();
        app.Configure(c => c.AddCommand<CapturingPushSettingsCommand>("push"));
        CapturingPushSettingsCommand.OnExecute = s => captured = s;

        var exitCode = app.Run(["push", "--force", "MySolution"]);

        exitCode.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.Force.Should().BeEquivalentTo(["MySolution"]);
        captured.Solution.Should().BeNull();
    }

    sealed class CapturingPushSettingsCommand : Command<PushCommand.Settings>
    {
        public static Action<PushCommand.Settings>? OnExecute;

        protected override int Execute(CommandContext context, PushCommand.Settings settings, CancellationToken cancellationToken)
        {
            OnExecute?.Invoke(settings);
            return 0;
        }
    }
}
