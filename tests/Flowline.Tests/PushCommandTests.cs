using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Config;
using Flowline.Core.Models;
using Flowline.Settings;
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

    // -- What each push command hands the engine --

    [Fact]
    public void BarePush_TargetsEverything_AndNamesNone()
    {
        var settings = new PushCommand.Settings();

        settings.Targets.Should().Be(PushTargets.All);
        settings.TargetNamed.Should().BeFalse();
        settings.AssemblyOnly.Should().BeFalse();
        settings.SkipPublish.Should().BeFalse();
    }

    [Fact]
    public void PushPlugins_TargetsPluginsOnly_AsANamedTarget()
    {
        var settings = new PushPluginsCommand.Settings();

        settings.Targets.Should().Be(PushTargets.Plugins);
        settings.TargetNamed.Should().BeTrue();
    }

    [Fact]
    public void PushPlugins_AssemblyOnlyFlag_ReachesTheEngine()
    {
        var settings = new PushPluginsCommand.Settings { AssemblyOnlyFlag = true };

        settings.AssemblyOnly.Should().BeTrue();
    }

    [Fact]
    public void PushWebResources_TargetsWebResourcesOnly_AndCarriesNoPublish()
    {
        var settings = new PushWebResourcesCommand.Settings { NoPublish = true };

        settings.Targets.Should().Be(PushTargets.WebResources);
        settings.TargetNamed.Should().BeTrue();
        settings.SkipPublish.Should().BeTrue();
    }

    // -- Stand-alone mode is keyed on --path, and only a subcommand has one --

    [Fact]
    public void IsStandaloneMode_BarePush_IsNeverStandalone()
    {
        PushSupport.IsStandaloneMode(new PushCommand.Settings()).Should().BeFalse();
    }

    [Fact]
    public void IsStandaloneMode_PushPluginsWithPath_ShouldReturnTrue()
    {
        var settings = new PushPluginsCommand.Settings { Path = "plugins.dll" };

        PushSupport.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void IsStandaloneMode_PushWebResourcesWithPath_ShouldReturnTrue()
    {
        var settings = new PushWebResourcesCommand.Settings { Path = "dist" };

        PushSupport.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void IsStandaloneMode_PushPluginsWithoutPath_ShouldReturnFalse()
    {
        PushSupport.IsStandaloneMode(new PushPluginsCommand.Settings()).Should().BeFalse();
    }

    // -- A named target the project doesn't have is an error; bare push skips it --

    [Fact]
    public void ThrowIfNamedTargetMissing_PushPlugins_ThrowsNotFound()
    {
        var act = () => PushSupport.ThrowIfNamedTargetMissing(new PushPluginsCommand.Settings(), PushSupport.NoPluginProjectMessage);

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.NotFound && e.Message == PushSupport.NoPluginProjectMessage);
    }

    [Fact]
    public void ThrowIfNamedTargetMissing_PushWebResources_ThrowsNotFound()
    {
        var act = () => PushSupport.ThrowIfNamedTargetMissing(new PushWebResourcesCommand.Settings(), PushSupport.NoWebResourcesProjectMessage);

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.NotFound && e.Message.Contains("flowline scaffold webresources"));
    }

    [Fact]
    public void ThrowIfNamedTargetMissing_BarePush_Skips()
    {
        var act = () => PushSupport.ThrowIfNamedTargetMissing(new PushCommand.Settings(), PushSupport.NoWebResourcesProjectMessage);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateStandaloneMode_WithFlowlineFile_ShouldThrowNamingPath()
    {
        File.WriteAllText(Path.Combine(_root, ".flowline"), "{}");

        var act = () => PushSupport.ValidateStandaloneMode(_root);

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("--path"));
    }

    [Fact]
    public void ValidateStandaloneMode_WithoutFlowlineFile_DoesNotThrow()
    {
        var act = () => PushSupport.ValidateStandaloneMode(_root);

        act.Should().NotThrow();
    }

    [Fact]
    public void ResolveStandaloneSolutionName_WithoutSolution_ShouldThrow()
    {
        var settings = new PushPluginsCommand.Settings { Path = "plugins.dll" };

        var act = () => PushSupport.ResolveStandaloneSolutionName(settings);

        act.Should().Throw<FlowlineException>().WithMessage("*--solution-name*");
    }

    // -- Project mode: a passed --solution-name is validated against the single configured solution --

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

        PushSupport.ResolveStandalonePluginFilePath(dll).Should().Be(Path.GetFullPath(dll));
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithNonDll_ShouldThrowNamingPath()
    {
        var file = Path.Combine(_root, "plugins.txt");
        File.WriteAllText(file, "");

        var act = () => PushSupport.ResolveStandalonePluginFilePath(file);

        act.Should().Throw<FlowlineException>().WithMessage("--path must point to a .dll or .nupkg file.");
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithNupkg_ShouldReturnFullPath()
    {
        // R2a/KD6: .nupkg is now accepted, routing to the same package entry point as project mode,
        // rather than the old "NuGet packages not yet supported" rejection.
        var file = Path.Combine(_root, "plugins.nupkg");
        File.WriteAllText(file, "");

        PushSupport.ResolveStandalonePluginFilePath(file).Should().Be(Path.GetFullPath(file));
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

        PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto).Should().Be(nupkg);
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

        PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Dll).Should().Be(dll);
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

        PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Nupkg).Should().Be(nupkg);
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

        PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto).Should().Be(dll);
    }

    [Fact]
    public void ResolvePluginPushPath_DllOnlyNupkgMode_ThrowsInsteadOfSilentlyFallingBackToDll()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, "");

        var act = () => PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Nupkg);

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

        PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto).Should().Be(dll);
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

        var act = () => PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto);

        act.Should().Throw<Flowline.Core.FlowlineException>()
            .WithMessage("*Plugins.1.0.0.nupkg*Plugins.1.0.1.nupkg*");
    }

    // -- NeedsRepack --
    // The stale package ClearStalePackages prevents on the build path is unreachable under --no-build:
    // nothing deletes the previous .nupkg, NuGet's Pack step never runs, and the leftover package pushes
    // as if it were the built code — matching the payload hash Dataverse already holds, so the push takes
    // the unchanged-content path and every step registration reconciles to "no changes" (issue #6).

    [Fact]
    public void NeedsRepack_StalePackageAfterOurOwnBuild_ShouldRepack()
    {
        var dll = WritePluginDll("built");
        var nupkg = WriteNupkg("Plugins.1.0.0.nupkg", "packed from older code");

        PushSupport.NeedsRepack(nupkg, dll, didBuild: true).Should().BeTrue();
    }

    [Fact]
    public void NeedsRepack_StalePackageUnderNoBuild_ThrowsInsteadOfPushingStaleContent()
    {
        var dll = WritePluginDll("built");
        var nupkg = WriteNupkg("Plugins.1.0.0.nupkg", "packed from older code");

        var act = () => PushSupport.NeedsRepack(nupkg, dll, didBuild: false);

        act.Should().Throw<Flowline.Core.FlowlineException>().WithMessage("*--no-build*");
    }

    [Fact]
    public void NeedsRepack_PackageCarriesTheBuiltAssembly_ShouldNotRepack()
    {
        var dll = WritePluginDll("built");
        var nupkg = WriteNupkg("Plugins.1.0.0.nupkg", "built");

        PushSupport.NeedsRepack(nupkg, dll, didBuild: false).Should().BeFalse();
    }

    [Fact]
    public void NeedsRepack_ClassicDllPush_ShouldNotRepack()
    {
        // No package to be stale against — the .dll IS what gets pushed.
        var dll = WritePluginDll("built");

        PushSupport.NeedsRepack(dll, dll, didBuild: false).Should().BeFalse();
    }

    string WritePluginDll(string content)
    {
        var publishDir = Path.Combine(_root, "bin", "Release", "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, content);
        return dll;
    }

    string WriteNupkg(string fileName, string dllContent)
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        Directory.CreateDirectory(buildOutputRoot);
        var nupkg = Path.Combine(buildOutputRoot, fileName);

        using var archive = ZipFile.Open(nupkg, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("lib/net462/Plugins.dll").Open());
        writer.Write(dllContent);

        return nupkg;
    }

    // -- ClearStalePackages --
    // dotnet pack embeds the version in the filename and never removes an earlier version's package, so
    // with MinVer (version moves on every commit) a normal `push` after a commit left two packages side
    // by side and the ambiguity guard above blocked the push. Measured: neither `-t:Rebuild` nor
    // `dotnet clean` removes the old one — `clean` removes the CURRENT package and leaves the stale one,
    // because MSBuild only tracks the outputs of the version it is building. Hence clearing here.

    [Fact]
    public void ClearStalePackages_RemovesEveryNupkgUnderTheBuildOutput()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, "");
        File.WriteAllText(Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg"), "");
        File.WriteAllText(Path.Combine(buildOutputRoot, "Plugins.1.0.1.nupkg"), "");
        File.WriteAllText(Path.Combine(publishDir, "Nested.2.0.0.nupkg"), "");

        PushSupport.ClearStalePackages(buildOutputRoot);

        Directory.GetFiles(buildOutputRoot, "*.nupkg", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(dll).Should().BeTrue("only packages are cleared — the build output itself stays");
    }

    [Fact]
    public void ClearStalePackages_ThenResolve_NoLongerHitsTheAmbiguityGuard()
    {
        // The end-to-end shape: two versions present, cleared, then the build writes exactly one.
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, "");
        File.WriteAllText(Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg"), "");
        File.WriteAllText(Path.Combine(buildOutputRoot, "Plugins.1.0.1.nupkg"), "");

        PushSupport.ClearStalePackages(buildOutputRoot);
        var repacked = Path.Combine(buildOutputRoot, "Plugins.1.0.2.nupkg");
        File.WriteAllText(repacked, ""); // stands in for what dotnet pack writes next

        PushSupport.ResolvePluginPushPath(dll, buildOutputRoot, PluginPackageMode.Auto).Should().Be(repacked);
    }

    [Fact]
    public void ClearStalePackages_ReportsWhatItRemoved()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        Directory.CreateDirectory(buildOutputRoot);
        File.WriteAllText(Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg"), "");

        var notes = new List<string>();
        PushSupport.ClearStalePackages(buildOutputRoot, notes.Add);

        notes.Should().ContainSingle(n => n.Contains("Plugins.1.0.0.nupkg"));
        // console.Verbose escapes what it is given, so a marked-up path would print its own tags.
        notes.Should().NotContain(n => n.Contains("[bold]"));
    }

    [Fact]
    public void ClearStalePackages_MissingBuildOutputRoot_DoesNotThrow()
    {
        var act = () => PushSupport.ClearStalePackages(Path.Combine(_root, "bin", "Release"));

        act.Should().NotThrow();
    }

    // -- U4: every confirmed project pushes in one invocation (R5, R6, KD4) --

    static PushSupport.PluginPushTarget Target(string projectName, string assemblyName, params string[] extraPackageAssemblies) =>
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

        PushSupport.ResolvePluginPushPath(salesDll, salesRoot, PluginPackageMode.Auto)
                   .Should().Be(Path.Combine(salesRoot, "Plugins.Sales.1.0.0.nupkg"));
        PushSupport.ResolvePluginPushPath(supportDll, supportRoot, PluginPackageMode.Auto)
                   .Should().Be(supportDll);
    }

    [Fact]
    public void ResolvePluginPushPath_TwoProjectsOneNupkgOneDllDllMode_ShouldResolveBothToTheirDll()
    {
        // The solution declared Dll, so the project that does produce a .nupkg follows the solution
        // rather than its own state — the whole point of KD4's single per-solution setting.
        var (salesRoot, salesDll) = WriteProjectOutput("Sales", "Plugins.Sales", withNupkg: true);
        var (supportRoot, supportDll) = WriteProjectOutput("Support", "Plugins.Support", withNupkg: false);

        PushSupport.ResolvePluginPushPath(salesDll, salesRoot, PluginPackageMode.Dll).Should().Be(salesDll);
        PushSupport.ResolvePluginPushPath(supportDll, supportRoot, PluginPackageMode.Dll).Should().Be(supportDll);
    }

    [Fact]
    public void ResolvePluginPushPath_TwoProjectsOneNupkgOneDllNupkgMode_ShouldThrowNamingTheProjectWithoutOne()
    {
        var (salesRoot, salesDll) = WriteProjectOutput("Sales", "Plugins.Sales", withNupkg: true);
        var (supportRoot, supportDll) = WriteProjectOutput("Support", "Plugins.Support", withNupkg: false);

        PushSupport.ResolvePluginPushPath(salesDll, salesRoot, PluginPackageMode.Nupkg)
                   .Should().Be(Path.Combine(salesRoot, "Plugins.Sales.1.0.0.nupkg"));

        var act = () => PushSupport.ResolvePluginPushPath(supportDll, supportRoot, PluginPackageMode.Nupkg);
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
        PushSupport.CollectPushedAssemblyNames([Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")])
                   .Should().BeEquivalentTo(["Sales", "Support"]);
    }

    [Fact]
    public void CollectPushedAssemblyNames_WithSingleProject_ShouldIncludeOnlyThatAssembly()
    {
        PushSupport.CollectPushedAssemblyNames([Target("Plugins", "Plugins")]).Should().BeEquivalentTo(["Plugins"]);
    }

    [Fact]
    public void CollectPushedAssemblyNames_WithMultiAssemblyPackage_ShouldIncludeItsNonPrimaryAssemblies()
    {
        // A .nupkg project alongside a classic one: the classic project's orphan sweep is solution-wide,
        // so it needs the package's other assemblies by name or it reads them as having no local source.
        PushSupport.CollectPushedAssemblyNames(
        [
            Target("Plugins.Sales", "Sales", "Sales.Shared", "Sales.Workflows"),
            Target("Plugins.Support", "Support"),
        ]).Should().BeEquivalentTo(["Sales", "Sales.Shared", "Sales.Workflows", "Support"]);
    }

    [Fact]
    public void CollectPushedAssemblyNames_WithTheSameAssemblyNameTwice_ShouldDedupeIgnoringCase()
    {
        PushSupport.CollectPushedAssemblyNames([Target("Plugins.A", "Shared"), Target("Plugins.B", "SHARED")])
                   .Should().ContainSingle();
    }

    // -- Per-project output: attributable at N=2, untouched at N=1 (R7) --

    [Fact]
    public void DescribePluginPushHeader_WithSingleProject_ShouldReturnNull()
    {
        // R7 regression guard: a one-project solution must print exactly what it printed before U4.
        PushSupport.DescribePluginPushHeader([Target("Plugins", "Plugins")], 0).Should().BeNull();
    }

    [Fact]
    public void DescribePluginPushHeader_WithTwoProjects_ShouldNumberAndNameEachProject()
    {
        PushSupport.PluginPushTarget[] targets = [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")];

        PushSupport.DescribePluginPushHeader(targets, 0).Should().Be("#1 [bold]Plugins.Sales[/] — pushing");
        PushSupport.DescribePluginPushHeader(targets, 1).Should().Be("#2 [bold]Plugins.Support[/] — pushing");
    }

    // -- Per-project failure: names which one, and what the org holds now --

    [Fact]
    public void DescribePluginPushFailure_WithSingleProject_ShouldReturnNull()
    {
        // R7: nothing to attribute, so the original exception reaches the user unwrapped.
        PushSupport.DescribePluginPushFailure([Target("Plugins", "Plugins")], 0, "Build failed.").Should().BeNull();
    }

    [Fact]
    public void DescribePluginPushFailure_WithSecondOfTwoFailing_ShouldNameItAndWhatAlreadyLanded()
    {
        PushSupport.PluginPushTarget[] targets = [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")];

        var message = PushSupport.DescribePluginPushFailure(targets, 1, "Assembly registration failed.");

        message.Should().Be("'Plugins.Support' failed to push: Assembly registration failed. " +
                            "Already in the org: Plugins.Sales. Fix 'Plugins.Support', then push again.");
    }

    [Fact]
    public void DescribePluginPushFailure_WithFirstOfThreeFailing_ShouldReportTheOnesNeverAttempted()
    {
        // The others aren't skipped silently — the message says they weren't tried, which is a different
        // state from "pushed" and the user has to be able to tell them apart.
        PushSupport.PluginPushTarget[] targets =
            [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support"), Target("Plugins.Ops", "Ops")];

        var message = PushSupport.DescribePluginPushFailure(targets, 0, "Assembly registration failed.");

        message.Should().Be("'Plugins.Sales' failed to push: Assembly registration failed. " +
                            "Not attempted: Plugins.Support, Plugins.Ops. Fix 'Plugins.Sales', then push again.");
    }

    [Fact]
    public void DescribePluginPushFailure_WithMiddleOfThreeFailing_ShouldReportBothPushedAndNotAttempted()
    {
        PushSupport.PluginPushTarget[] targets =
            [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support"), Target("Plugins.Ops", "Ops")];

        PushSupport.DescribePluginPushFailure(targets, 1, "Assembly registration failed.")
                   .Should().Contain("Already in the org: Plugins.Sales.")
                   .And.Contain("Not attempted: Plugins.Ops.");
    }

    [Fact]
    public void DescribePluginPushFailure_WithUnpunctuatedReason_ShouldCloseTheSentence()
    {
        PushSupport.PluginPushTarget[] targets = [Target("Plugins.Sales", "Sales"), Target("Plugins.Support", "Support")];

        PushSupport.DescribePluginPushFailure(targets, 0, "Connection reset by peer")
                   .Should().Contain("Connection reset by peer. Not attempted:");
    }

    // -- IsPackagePush (KD6 — shared routing decision for project mode and standalone) --

    [Fact]
    public void IsPackagePush_WithNupkgPath_ShouldReturnTrue()
    {
        PushSupport.IsPackagePush(Path.Combine(_root, "Plugins.nupkg")).Should().BeTrue();
    }

    [Fact]
    public void IsPackagePush_WithDllPath_ShouldReturnFalse()
    {
        PushSupport.IsPackagePush(Path.Combine(_root, "Plugins.dll")).Should().BeFalse();
    }

    // -- BuildProjectPushTarget: project mode must reflect its own .nupkg --
    // Project mode used to leave ReflectedAssemblies null, so CollectPushedAssemblyNames could never see
    // a package's non-primary assemblies — the sibling projects' orphan sweeps then read them as having
    // no local source. Driven through the production builder with a real .nupkg, not a hand-built target.

    [Fact]
    public void BuildProjectPushTarget_MultiAssemblyPackage_ShouldReflectEveryAssemblyInIt()
    {
        CopyXrmSdkDllNextTo(_root);
        var primary = BuildPluginDll(_root, "Sales", "Sales.SomePlugin");
        var secondary = BuildPluginDll(_root, "Sales.Shared", "Sales.Shared.OtherPlugin");
        var nupkg = Path.Combine(_root, "Sales.1.0.0.nupkg");
        BuildNupkg(nupkg, primary, secondary);

        var target = PushSupport.BuildProjectPushTarget(nupkg, "Sales", "Plugins.Sales", new Spectre.Console.Testing.TestConsole());

        target.AssemblyName.Should().Be("Sales");
        target.ReflectedAssemblies.Should().NotBeNull();
        target.ReflectedAssemblies!.Select(a => a.Name).Should().BeEquivalentTo(["Sales", "Sales.Shared"]);
    }

    [Fact]
    public void CollectPushedAssemblyNames_WithAProjectModePackageTarget_ShouldIncludeItsNonPrimaryAssemblies()
    {
        // End-to-end for the set Finding 1's fix depends on: a real .nupkg through the real builder,
        // beside a classic sibling — every name the push owns has to come out.
        CopyXrmSdkDllNextTo(_root);
        var primary = BuildPluginDll(_root, "Sales", "Sales.SomePlugin");
        var secondary = BuildPluginDll(_root, "Sales.Shared", "Sales.Shared.OtherPlugin");
        var nupkg = Path.Combine(_root, "Sales.1.0.0.nupkg");
        BuildNupkg(nupkg, primary, secondary);

        var packageTarget = PushSupport.BuildProjectPushTarget(nupkg, "Sales", "Plugins.Sales", new Spectre.Console.Testing.TestConsole());

        PushSupport.CollectPushedAssemblyNames([packageTarget, Target("Plugins.Support", "Support")])
                   .Should().BeEquivalentTo(["Sales", "Sales.Shared", "Support"]);
    }

    [Fact]
    public void BuildProjectPushTarget_ClassicDll_ShouldNotReflectAnything()
    {
        // R7: the classic single-assembly shape stays exactly as it was — no reflection, no extra names.
        var dll = Path.Combine(_root, "Support.dll");
        File.WriteAllText(dll, "");

        var target = PushSupport.BuildProjectPushTarget(dll, "Support", "Plugins.Support", new Spectre.Console.Testing.TestConsole());

        target.ReflectedAssemblies.Should().BeNull();
        PushSupport.CollectPushedAssemblyNames([target]).Should().BeEquivalentTo(["Support"]);
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

        var (name, assemblies) = PushSupport.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

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

        var act = () => PushSupport.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

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

        var (name, assemblies) = PushSupport.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

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
        PushSupport.ResolveStandaloneWebResourcesPath(folder).Should().Be(Path.GetFullPath(folder));
    }

    [Fact]
    public void EnsureBuiltWebResources_WithFiles_ShouldNotThrow()
    {
        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);
        File.WriteAllText(Path.Combine(dist, "account.js"), "");

        var act = () => PushSupport.EnsureBuiltWebResources(dist);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithNestedFiles_ShouldNotThrow()
    {
        var dist = Path.Combine(_root, "dist");
        var sub = Path.Combine(dist, "forms");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "form.js"), "");

        var act = () => PushSupport.EnsureBuiltWebResources(dist);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithEmptyFolder_ShouldThrow()
    {
        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);

        var act = () => PushSupport.EnsureBuiltWebResources(dist);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithMissingFolder_ShouldThrow()
    {
        var dist = Path.Combine(_root, "dist");

        var act = () => PushSupport.EnsureBuiltWebResources(dist);

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

    // Each target accepts only the hazards it can hit; bare push runs both, so it accepts them all.
    [Theory]
    [InlineData("delete-orphans")]
    [InlineData("recreate-assembly")]
    [InlineData("config")]
    [InlineData("all")]
    public void ValidateForce_PushPlugins_AcceptsItsOwnHazards(string value)
    {
        var act = () => FlowlineSettings.ValidateForce([value], PushPluginsCommand.ValidSpecifiers, "push plugins");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateForce_PushPlugins_RejectsAWebResourcesHazard_NamingTheCommand()
    {
        var act = () => FlowlineSettings.ValidateForce(["delete-form-handlers"], PushPluginsCommand.ValidSpecifiers, "push plugins");

        act.Should().Throw<FlowlineException>()
            .WithMessage("'delete-form-handlers' isn't a valid --force value for 'push plugins'. Use one of: delete-orphans, recreate-assembly, config, all.");
    }

    [Theory]
    [InlineData("delete-form-handlers")]
    [InlineData("config")]
    [InlineData("all")]
    public void ValidateForce_PushWebResources_AcceptsItsOwnHazards(string value)
    {
        var act = () => FlowlineSettings.ValidateForce([value], PushWebResourcesCommand.ValidSpecifiers, "push webresources");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateForce_PushWebResources_RejectsAPluginHazard()
    {
        var act = () => FlowlineSettings.ValidateForce(["recreate-assembly"], PushWebResourcesCommand.ValidSpecifiers, "push webresources");

        act.Should().Throw<FlowlineException>()
            .WithMessage("'recreate-assembly' isn't a valid --force value for 'push webresources'. Use one of: delete-form-handlers, config, all.");
    }

    [Fact]
    public void ValidSpecifiers_BarePush_IsEveryTargetsHazards()
    {
        PushCommand.ValidSpecifiers.Should().BeEquivalentTo(
            PushPluginsCommand.ValidSpecifiers.Union(PushWebResourcesCommand.ValidSpecifiers));
    }

    // -- HasForce wiring (R6) --

    [Fact]
    public void HasForce_DeleteOrphansOnly_DoesNotApproveRecreateAssembly()
    {
        var settings = new PushCommand.Settings { Force = ["delete-orphans"] };
        var options = new FlowlineRuntimeOptions { Force = settings.Force };

        options.HasForce("delete-orphans").Should().BeTrue();
        options.HasForce("recreate-assembly").Should().BeFalse();
        options.HasForce("delete-form-handlers").Should().BeFalse();
    }

    [Fact]
    public void HasForce_All_ApprovesEveryPushHazard()
    {
        var settings = new PushCommand.Settings { Force = ["all"] };
        var options = new FlowlineRuntimeOptions { Force = settings.Force };

        options.HasForce("delete-orphans").Should().BeTrue();
        options.HasForce("recreate-assembly").Should().BeTrue();
        options.HasForce("delete-form-handlers").Should().BeTrue();
        options.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void HasForce_RepeatedFlag_ApprovesExactlyThoseTwo()
    {
        var settings = new PushCommand.Settings { Force = ["delete-orphans", "config"] };
        var options = new FlowlineRuntimeOptions { Force = settings.Force };

        options.HasForce("delete-orphans").Should().BeTrue();
        options.HasForce("config").Should().BeTrue();
        options.HasForce("recreate-assembly").Should().BeFalse();
        options.HasForce("delete-form-handlers").Should().BeFalse();
    }

    // -- CommandApp parse seam — exit-code-only per KTD3's test-seam split; Spectre renders parse
    // errors via AnsiConsole, not Console.Out, so message content isn't capturable via simple
    // redirection (confirmed during planning). The probe registers the push branch the way
    // Program.cs does, with capturing commands in place of the real ones, so Spectre's real parser
    // runs against the real settings shapes without the commands' full DI graph, which parsing
    // itself never touches. Strict parsing on, as the real CLI runs.

    sealed class CapturingCommand<TSettings> : Command<TSettings> where TSettings : CommandSettings
    {
        public static TSettings? Captured;

        public override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    static CommandApp BuildPushParseProbe()
    {
        CapturingCommand<PushCommand.Settings>.Captured = null;
        CapturingCommand<PushPluginsCommand.Settings>.Captured = null;
        CapturingCommand<PushWebResourcesCommand.Settings>.Captured = null;

        var app = new CommandApp();
        app.Configure(c =>
        {
            c.Settings.StrictParsing = true;
            c.AddBranch<PushSettings>("push", push =>
            {
                push.SetDefaultCommand<CapturingCommand<PushCommand.Settings>>();
                push.AddCommand<CapturingCommand<PushPluginsCommand.Settings>>(PushPluginsCommand.Name);
                push.AddCommand<CapturingCommand<PushWebResourcesCommand.Settings>>(PushWebResourcesCommand.Name)
                    .WithAlias(PushWebResourcesCommand.Alias);
            });
        });
        return app;
    }

    [Fact]
    public void CommandApp_BareForce_NoValue_FailsToParse()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "--force"]);

        exitCode.Should().NotBe(0);
    }

    [Fact]
    public void CommandApp_ForceRepeated_CollectsBothValues()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "--force", "delete-orphans", "--force", "config"]);

        exitCode.Should().Be(0);
        CapturingCommand<PushCommand.Settings>.Captured!.Force.Should().BeEquivalentTo(["delete-orphans", "config"]);
    }

    [Fact]
    public void CommandApp_BarePushWithSharedOptions_RoutesToBarePush()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "--dry-run", "-e", "dev", "--no-build"]);

        exitCode.Should().Be(0);
        var captured = CapturingCommand<PushCommand.Settings>.Captured!;
        captured.DryRun.Should().BeTrue();
        captured.Env.Should().Be("dev");
        captured.NoBuild.Should().BeTrue();
    }

    [Theory]
    [InlineData("push", "plugins", "--dry-run")]
    [InlineData("push", "--dry-run", "plugins")]
    public void CommandApp_SharedOption_BindsOnEitherSideOfTheSubcommand(params string[] args)
    {
        var exitCode = BuildPushParseProbe().Run(args);

        exitCode.Should().Be(0);
        CapturingCommand<PushPluginsCommand.Settings>.Captured!.DryRun.Should().BeTrue();
        CapturingCommand<PushCommand.Settings>.Captured.Should().BeNull();
    }

    [Fact]
    public void CommandApp_ForceValueBeforeSubcommand_BindsAsForceValue()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "--force", "delete-orphans", "plugins"]);

        exitCode.Should().Be(0);
        CapturingCommand<PushPluginsCommand.Settings>.Captured!.Force.Should().BeEquivalentTo(["delete-orphans"]);
    }

    [Fact]
    public void CommandApp_PushPlugins_BindsPathAndAssemblyOnly()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "plugins", "-p", "./bin/Release/Plugins.dll", "--assembly-only", "-n", "Contoso"]);

        exitCode.Should().Be(0);
        var captured = CapturingCommand<PushPluginsCommand.Settings>.Captured!;
        captured.Path.Should().Be("./bin/Release/Plugins.dll");
        captured.AssemblyOnly.Should().BeTrue();
        captured.SolutionName.Should().Be("Contoso");
    }

    [Theory]
    [InlineData("webresources")]
    [InlineData("wr")]
    public void CommandApp_PushWebResources_ByNameOrAlias_BindsPathAndNoPublish(string name)
    {
        var exitCode = BuildPushParseProbe().Run(["push", name, "--path", "./dist", "--no-publish"]);

        exitCode.Should().Be(0);
        var captured = CapturingCommand<PushWebResourcesCommand.Settings>.Captured!;
        captured.Path.Should().Be("./dist");
        captured.SkipPublish.Should().BeTrue();
    }

    // Target-only options exist only on their target, and the removed options exist nowhere.
    [Theory]
    [InlineData("push", "--assembly-only")]
    [InlineData("push", "--no-publish")]
    [InlineData("push", "-p", "x.dll")]
    [InlineData("push", "plugins", "--no-publish")]
    [InlineData("push", "wr", "--assembly-only")]
    [InlineData("push", "--scope", "plugins")]
    [InlineData("push", "--plugin-file", "x.dll")]
    [InlineData("push", "--webresources", "./dist")]
    [InlineData("push", "-w", "./dist")]
    [InlineData("push", "plugins", "--scope", "plugins")]
    public void CommandApp_OptionOnTheWrongCommand_FailsToParse(params string[] args)
    {
        var exitCode = BuildPushParseProbe().Run(args);

        exitCode.Should().NotBe(0);
    }

    [Theory]
    [InlineData("--solution-name")]
    [InlineData("-n")]
    public void CommandApp_SolutionNameFlag_BindsSolutionName(string flag)
    {
        var exitCode = BuildPushParseProbe().Run(["push", flag, "Contoso"]);

        exitCode.Should().Be(0);
        CapturingCommand<PushCommand.Settings>.Captured!.SolutionName.Should().Be("Contoso");
    }

    // The old shape: a bare word after the verb. Strict parsing, as the real CLI runs, so it fails
    // instead of being absorbed.
    [Fact]
    public void CommandApp_SolutionAsPositional_FailsToParse()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "Contoso"]);

        exitCode.Should().NotBe(0);
    }

    // -- R1/R10: --env replaces --dev (U3) --

    [Fact]
    public void CommandApp_EnvShortFlagWithUrl_BindsEnv()
    {
        var exitCode = BuildPushParseProbe().Run(["push", "-e", "https://contoso-dev.crm4.dynamics.com"]);

        exitCode.Should().Be(0);
        CapturingCommand<PushCommand.Settings>.Captured!.Env.Should().Be("https://contoso-dev.crm4.dynamics.com");
    }

    [Fact]
    public void Settings_HasNoDevUrlProperty_TheOldFlagIsGone()
    {
        // R10/KD5: --dev is removed with no alias — asserted on the settings shape. Production strict
        // parsing (Program.cs, via CliConfiguration.Configure) is proven in ProgramParsingTests.
        typeof(PushSettings).GetProperty("DevUrl").Should().BeNull();
    }
}
