using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using Flowline.Commands;
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
    public void ResolvePluginPushPath_DllAndNupkgNoForce_ShouldReturnNupkg()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        var nupkg = Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg");
        File.WriteAllText(dll, "");
        File.WriteAllText(nupkg, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, forceClassicPluginAssembly: false).Should().Be(nupkg);
    }

    [Fact]
    public void ResolvePluginPushPath_DllAndNupkgWithForceClassic_ShouldReturnDll()
    {
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        var nupkg = Path.Combine(buildOutputRoot, "Plugins.1.0.0.nupkg");
        File.WriteAllText(dll, "");
        File.WriteAllText(nupkg, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, forceClassicPluginAssembly: true).Should().Be(dll);
    }

    [Fact]
    public void ResolvePluginPushPath_DllOnly_ShouldReturnDll()
    {
        // Regression guard: most existing plugin projects don't produce a .nupkg yet.
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var publishDir = Path.Combine(buildOutputRoot, "net462", "publish");
        Directory.CreateDirectory(publishDir);
        var dll = Path.Combine(publishDir, "Plugins.dll");
        File.WriteAllText(dll, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, forceClassicPluginAssembly: false).Should().Be(dll);
    }

    [Fact]
    public void ResolvePluginPushPath_MissingBuildOutputRoot_ShouldReturnDll()
    {
        // --no-build with a stale/missing bin folder shouldn't throw here — the earlier File.Exists(dll)
        // check in PreparePluginsForPushAsync already guards the real "nothing built" case.
        var buildOutputRoot = Path.Combine(_root, "bin", "Release");
        var dll = Path.Combine(_root, "Plugins.dll");
        File.WriteAllText(dll, "");

        PushCommand.ResolvePluginPushPath(dll, buildOutputRoot, forceClassicPluginAssembly: false).Should().Be(dll);
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

        var name = PushCommand.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

        name.Should().Be("MyPlugins");
        name.Should().NotBe("MyPlugins.1.0.0");
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

        act.Should().Throw<Flowline.FlowlineException>()
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

        var name = PushCommand.ResolveStandalonePackageAssemblyName(nupkgPath, new Spectre.Console.Testing.TestConsole());

        name.Should().Be("Empty.1.0.0");
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
