using System.Reflection;
using System.Reflection.Emit;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Plugins;
using FluentAssertions;
using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Tests;

public class PluginProjectResolverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public PluginProjectResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- U1: candidate enumeration from the solution file ----

    [Fact]
    public void EnumerateCandidates_WithOnePluginProject_ShouldYieldThatCandidate()
    {
        var projectPath = WriteProject("Plugins", "Plugins.csproj", PluginProjectXml());

        var candidates = PluginProjectResolver.EnumerateCandidates(
            [SolutionEntry("Plugins", @"Plugins\Plugins.csproj")], _root);

        candidates.Should().ContainSingle();
        candidates[0].ProjectPath.Should().Be(projectPath);
        candidates[0].ProjectName.Should().Be("Plugins");
        candidates[0].BuildOutputRoot.Should().Be(Path.Combine(_root, "Plugins", "bin", "Release"));
    }

    [Fact]
    public void EnumerateCandidates_WithTwoPluginProjects_ShouldYieldBoth()
    {
        WriteProject("Sales", "Plugins.Sales.csproj", PluginProjectXml());
        WriteProject("Support", "Plugins.Support.csproj", PluginProjectXml());

        var candidates = PluginProjectResolver.EnumerateCandidates(
        [
            SolutionEntry("Plugins.Sales", @"Sales\Plugins.Sales.csproj"),
            SolutionEntry("Plugins.Support", @"Support\Plugins.Support.csproj"),
        ], _root);

        candidates.Select(c => c.ProjectName).Should().BeEquivalentTo(["Plugins.Sales", "Plugins.Support"]);
    }

    [Fact]
    public void EnumerateCandidates_WithCdsprojAndNonPluginCsproj_ShouldYieldOnlyCsprojEntries()
    {
        WriteProject("Plugins", "Plugins.csproj", PluginProjectXml());
        WriteProject("Entities", "Entities.csproj", DtoProjectXml());
        WriteProject("Solution", "DWE_Base.cdsproj", "<Project />");

        var candidates = PluginProjectResolver.EnumerateCandidates(
        [
            SolutionEntry("Plugins", @"Plugins\Plugins.csproj"),
            SolutionEntry("Entities", @"Entities\Entities.csproj"),
            SolutionEntry("DWE_Base", @"Solution\DWE_Base.cdsproj"),
        ], _root);

        candidates.Select(c => c.ProjectName).Should().BeEquivalentTo(["Plugins", "Entities"]);
    }

    [Fact]
    public void EnumerateCandidates_WithProjectMissingOnDisk_ShouldThrowNamingThePath()
    {
        var act = () => PluginProjectResolver.EnumerateCandidates(
            [SolutionEntry("Ghost", @"Ghost\Ghost.csproj")], _root);

        act.Should().Throw<FlowlineException>()
           .Which.Message.Should().Contain("Ghost").And.Contain("Ghost.csproj");
    }

    [Fact]
    public void EnumerateCandidates_WithEntriesInReverseOrder_ShouldReturnTheSameOrder()
    {
        WriteProject("Sales", "Plugins.Sales.csproj", PluginProjectXml());
        WriteProject("Support", "Plugins.Support.csproj", PluginProjectXml());

        MsBuildSolutionProject[] forward =
        [
            SolutionEntry("Plugins.Sales", @"Sales\Plugins.Sales.csproj"),
            SolutionEntry("Plugins.Support", @"Support\Plugins.Support.csproj"),
        ];

        PluginProjectResolver.EnumerateCandidates(forward, _root).Select(c => c.ProjectPath)
            .Should().Equal(PluginProjectResolver.EnumerateCandidates(forward.Reverse().ToList(), _root).Select(c => c.ProjectPath));
    }

    // ---- KTD4: pre-filter, and nothing it drops is silent ----

    [Fact]
    public void DescribePreFilterSkip_WithPluginProject_ShouldReturnNull()
    {
        var projectPath = WriteProject("Plugins", "Plugins.csproj", PluginProjectXml());

        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().BeNull();
    }

    [Fact]
    public void DescribePreFilterSkip_WithWebResourcesProject_ShouldReportNoSdkReference()
    {
        // WebResources.csproj wraps an npm build and produces no assembly at all.
        var projectPath = WriteProject("WebResources", "WebResources.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup></Project>");

        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().Contain("Microsoft.Xrm.Sdk");
    }

    [Fact]
    public void DescribePreFilterSkip_WithModernTargetFramework_ShouldReportTheFramework()
    {
        var projectPath = WriteProject("Api", "Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><PackageReference Include=\"Microsoft.Xrm.Sdk\" Version=\"9.0.0\" /></ItemGroup></Project>");

        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().Contain("net10.0");
    }

    [Fact]
    public void DescribePreFilterSkip_WithoutTargetFrameworkElement_ShouldReturnNull()
    {
        // The framework can come from a props file this pre-filter never reads — let it through rather
        // than drop a real plugin project.
        var projectPath = WriteProject("Plugins", "Plugins.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Flowline.Attributes\" Version=\"1.0.0\" /></ItemGroup></Project>");

        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().BeNull();
    }

    [Fact]
    public void DescribePreFilterSkip_WithNoSdkMarkerButAProjectReference_ShouldDeferToReflection()
    {
        // The SDK can arrive transitively through a shared base library, so the marker's absence from this
        // csproj proves nothing. The filter must hand the project to reflection, not decide.
        var projectPath = WriteProject("Sales", "Sales.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\Base\\Base.csproj\" /></ItemGroup></Project>");

        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().BeNull();
    }

    [Fact]
    public void DescribePreFilterSkip_WithNoSdkMarkerButADirectoryBuildProps_ShouldDeferToReflection()
    {
        // Centralised PackageReferences in Directory.Build.props leave a real plugin project's own csproj
        // marker-free.
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        var projectPath = WriteProject("Sales", "Sales.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup></Project>");

        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().BeNull();
    }

    [Fact]
    public void DescribePreFilterSkip_WithNoSdkMarkerNoProjectReferenceAndNoProps_ShouldStillDrop()
    {
        // The filter has to stay useful: with nothing that could smuggle the SDK in, the absence of a
        // marker IS confident, and the project is dropped without a build.
        var projectPath = WriteProject("Docs", "Docs.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup></Project>");

        Directory.EnumerateFiles(_root, "Directory.Build.props", SearchOption.AllDirectories).Should().BeEmpty();
        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().Contain("Microsoft.Xrm.Sdk");
    }

    [Fact]
    public void DescribePreFilterSkip_WithModernTargetFrameworkAndAProjectReference_ShouldStillDrop()
    {
        // The deferral covers the SDK-marker check only. <TargetFramework> is the project's own
        // declaration — nothing a props file or a project reference adds makes net10.0 a plugin project,
        // and this is the shape the scaffolded WebResources.csproj has.
        var projectPath = WriteProject("WebResources", "WebResources.csproj",
            "<Project Sdk=\"Microsoft.Build.NoTargets/3.7.134\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\Base\\Base.csproj\" /></ItemGroup></Project>");

        PluginProjectResolver.DescribePreFilterSkip(projectPath).Should().Contain("net10.0");
    }

    // ---- U2: generalized build-output resolution ----

    [Fact]
    public void FindOutputAssemblies_WithDefaultShape_ShouldPutTheProjectNamedAssemblyFirst()
    {
        var candidate = Candidate("Plugins", "Plugins.csproj");
        var outputDir = CreateOutput(candidate, "net462", "publish");
        BuildDependencyDll(outputDir, "Newtonsoft.Json", "Newtonsoft.Json.JsonConvert");
        var pluginsDll = BuildPluginDll(outputDir, "Plugins", "Acme.AccountPostCreatePlugin");

        PluginProjectResolver.FindOutputAssemblies(candidate)[0].Should().Be(pluginsDll);
    }

    [Fact]
    public void FindOutputAssemblies_WithBothNetFolderAndPublishFolder_ShouldPreferThePublishCopy()
    {
        var candidate = Candidate("Plugins", "Plugins.csproj");
        BuildPluginDll(CreateOutput(candidate, "net462"), "Plugins", "Acme.AccountPostCreatePlugin");
        var publishDll = BuildPluginDll(CreateOutput(candidate, "net462", "publish"), "Plugins", "Acme.AccountPostCreatePlugin");

        var assemblies = PluginProjectResolver.FindOutputAssemblies(candidate);

        assemblies.Should().ContainSingle().Which.Should().Be(publishDll);
    }

    [Fact]
    public void FindOutputAssemblies_WithSpotlerShape_ShouldFindTheCustomlyNamedAssembly()
    {
        // <AssemblyName>AV.SpotlerAutomate.Plugins</AssemblyName>, no packaging, plain `dotnet build`
        // output straight at bin/Release/net462/ with no publish subfolder. None of the three names the
        // old fixed path assumed hold here.
        var candidate = Candidate("Plugins", "Plugins.csproj");
        var outputDir = CreateOutput(candidate, "net462");
        var pluginsDll = BuildPluginDll(outputDir, "AV.SpotlerAutomate.Plugins", "AV.Spotler.ContactPostCreatePlugin");
        CopyXrmSdkDllNextTo(outputDir);

        PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { }).Should().Be(pluginsDll);
    }

    [Fact]
    public void FindOutputAssemblies_WithoutTargetFrameworkFolder_ShouldResolve()
    {
        // AppendTargetFrameworkToOutputPath=false drops the assembly straight into bin/Release.
        var candidate = Candidate("Plugins", "Plugins.csproj");
        var outputDir = CreateOutput(candidate);
        var pluginsDll = BuildPluginDll(outputDir, "Plugins", "Acme.AccountPostCreatePlugin");
        CopyXrmSdkDllNextTo(outputDir);

        PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { }).Should().Be(pluginsDll);
    }

    [Fact]
    public void FindOutputAssemblies_WithUnbuiltProject_ShouldThrowBuildFirst()
    {
        var candidate = Candidate("Plugins", "Plugins.csproj");

        var act = () => PluginProjectResolver.FindOutputAssemblies(candidate);

        act.Should().Throw<FlowlineException>()
           .Which.Message.Should().Contain("Build it first").And.Contain("Plugins");
    }

    // ---- undeterminable candidates fail the push instead of being guessed away ----

    [Fact]
    public void FindOutputAssemblies_WithNothingBuiltUnderNoBuild_ShouldThrowNamingTheProjectAndStandaloneMode()
    {
        // --no-build with an empty bin/Release: reflection is the only thing that could have said whether
        // this is a plugin project, and it never ran. Skipping would hand the orphan sweeps a project with
        // "no local source" and delete its live registration, so the push stops instead.
        var candidate = Candidate("SomeLibrary", "SomeLibrary.csproj");

        var act = () => PluginProjectResolver.FindOutputAssemblies(candidate);

        var message = act.Should().Throw<FlowlineException>()
                         .Which.Message;
        message.Should().Contain("SomeLibrary").And.Contain("--pluginFile");
    }

    [Fact]
    public void ResolvePluginAssembly_WithNothingBuiltUnderNoBuild_ShouldThrowRatherThanReturnNull()
    {
        var candidate = Candidate("SomeLibrary", "SomeLibrary.csproj");

        var act = () => PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { });

        act.Should().Throw<FlowlineException>().Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    [Fact]
    public void ResolvePluginAssembly_WithNoAssemblyItCanLoad_ShouldThrowNamingTheProjectAndStandaloneMode()
    {
        // The classic trigger: a real plugin project whose Microsoft.Xrm.Sdk.dll isn't copy-local. The
        // IPlugin base type won't resolve, so reflection reports "no plugin types" for a project that has
        // them. That verdict is a guess, and acting on it deletes the live assembly, steps, and Custom
        // APIs — so it errors instead.
        var candidate = Candidate("Plugins", "Plugins.csproj");
        BuildPluginDll(CreateOutput(candidate, "net462"), "Plugins", "Acme.AccountPostCreatePlugin");

        var act = () => PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { });

        var thrown = act.Should().Throw<FlowlineException>().Which;
        thrown.ExitCode.Should().Be(ExitCode.ValidationFailed);
        thrown.Message.Should().Contain("Plugins").And.Contain("--pluginFile");
    }

    [Fact]
    public void ResolvePluginAssembly_WithOneLoadableAssemblyAmongUnloadableOnes_ShouldSkipSilentlyInsteadOfThrowing()
    {
        // A dependency DLL that can't be reflected on its own is ordinary, not a failure — one clean load
        // is enough to make "no plugin types here" a real verdict rather than a guess.
        var candidate = Candidate("Entities", "Entities.csproj");
        var outputDir = CreateOutput(candidate, "net462");
        BuildDependencyDll(outputDir, "Entities", "Acme.AccountDto");
        File.WriteAllText(Path.Combine(outputDir, "Native.dll"), "not a PE image");

        PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { }).Should().BeNull();
    }

    // ---- U3: confirmation by reflection ----

    [Fact]
    public void ResolvePluginAssembly_WithIPluginImplementation_ShouldConfirmIt()
    {
        var candidate = Candidate("Plugins", "Plugins.csproj");
        var outputDir = CreateOutput(candidate, "net462", "publish");
        var pluginsDll = BuildPluginDll(outputDir, "Plugins", "Acme.AccountPostCreatePlugin");
        CopyXrmSdkDllNextTo(outputDir);

        PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { }).Should().Be(pluginsDll);
    }

    [Fact]
    public void ResolvePluginAssembly_WithCodeActivityOnly_ShouldConfirmIt()
    {
        var candidate = Candidate("Workflows", "Workflows.csproj");
        var outputDir = CreateOutput(candidate, "net462");
        var workflowDll = BuildCodeActivityDll(outputDir, "Workflows", "Acme.CalculateDiscount");

        PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { }).Should().Be(workflowDll);
    }

    [Fact]
    public void ResolvePluginAssembly_WithDtoLibraryOnly_ShouldReturnNullAndReportEveryAssembly()
    {
        var candidate = Candidate("Entities", "Entities.csproj");
        var outputDir = CreateOutput(candidate, "net462");
        BuildDependencyDll(outputDir, "Entities", "Acme.AccountDto");
        BuildDependencyDll(outputDir, "Newtonsoft.Json", "Newtonsoft.Json.JsonConvert");

        var notes = new List<string>();

        PluginProjectResolver.ResolvePluginAssembly(candidate, notes.Add).Should().BeNull();
        notes.Should().HaveCount(2);
        notes.Should().OnlyContain(n => n.Contains("no IPlugin or CodeActivity type"));
    }

    [Fact]
    public void ConfirmsPluginTypes_WithAFileThatIsNotAnAssembly_ShouldReportTheFailure()
    {
        var notAnAssembly = Path.Combine(_root, "garbage.dll");
        File.WriteAllText(notAnAssembly, "this is not a PE image");

        PluginProjectResolver.ConfirmsPluginTypes(notAnAssembly, out var failure).Should().BeFalse();
        failure.Should().NotBeNullOrWhiteSpace();
    }

    // ---- U5: the shared discovery entry point non-push consumers use ----

    [Fact]
    public async Task DiscoverAsync_WithSolutionFile_ShouldReturnPreFilteredCandidates()
    {
        WriteProject("Sales", "AV.Sales.Plugins.csproj", PluginProjectXml());
        WriteProject("Entities", "Entities.csproj", DtoProjectXml());
        WriteSolution(@"Sales\AV.Sales.Plugins.csproj", @"Entities\Entities.csproj");

        var candidates = await PluginProjectResolver.DiscoverAsync(_root);

        candidates.Select(c => c.ProjectName).Should().BeEquivalentTo(["AV.Sales.Plugins"]);
    }

    [Fact]
    public async Task DiscoverAsync_WithTwoPluginProjects_ShouldReturnBoth()
    {
        WriteProject("Sales", "Sales.Plugins.csproj", PluginProjectXml());
        WriteProject("Support", "Support.Plugins.csproj", PluginProjectXml());
        WriteSolution(@"Sales\Sales.Plugins.csproj", @"Support\Support.Plugins.csproj");

        var candidates = await PluginProjectResolver.DiscoverAsync(_root);

        candidates.Should().HaveCount(2);
        candidates.Select(c => c.BuildOutputRoot).Should().BeEquivalentTo(
        [
            Path.Combine(_root, "Sales", "bin", "Release"),
            Path.Combine(_root, "Support", "bin", "Release")
        ]);
    }

    [Fact]
    public async Task DiscoverAsync_WithNoSolutionFile_ShouldFallBackToTheConventionalProject()
    {
        var candidates = await PluginProjectResolver.DiscoverAsync(_root);

        candidates.Should().ContainSingle();
        candidates[0].ProjectName.Should().Be("Plugins");
        candidates[0].ProjectPath.Should().Be(Path.Combine(_root, "Plugins", "Plugins.csproj"));
        candidates[0].BuildOutputRoot.Should().Be(Path.Combine(_root, "Plugins", "bin", "Release"));
    }

    [Fact]
    public async Task DiscoverAsync_WithNoSolutionFileAndNoPluginsFolder_ShouldNotThrow()
    {
        // The partially-set-up repo the drift check has to survive.
        var act = async () => await PluginProjectResolver.DiscoverAsync(_root);

        await act.Should().NotThrowAsync();
    }

    // ---- the common case: an ordinary multi-project solution still pushes ----

    [Fact]
    public void ResolvePluginAssembly_AcrossAnOrdinaryMultiProjectSolution_ShouldResolveOneAndSkipTheRestSilently()
    {
        // The regression this whole change must not cause. One plugin project alongside WebResources, a
        // test project, a marker-free DTO library, and a shared library that only reaches the SDK through
        // a ProjectReference. Every non-plugin project has to fall out — pre-filtered or definitively
        // reflected away — without a single throw, exactly as it does today.
        WriteProject("Plugins", "Plugins.csproj", PluginProjectXml());
        WriteProject("WebResources", "WebResources.csproj",
            "<Project Sdk=\"Microsoft.Build.NoTargets/3.7.134\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        WriteProject("Tests", "Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        WriteProject("Entities", "Entities.csproj", DtoProjectXml());
        WriteProject("Shared", "Shared.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\Entities\\Entities.csproj\" /></ItemGroup></Project>");

        var candidates = PluginProjectResolver.EnumerateCandidates(
        [
            SolutionEntry("Plugins", @"Plugins\Plugins.csproj"),
            SolutionEntry("WebResources", @"WebResources\WebResources.csproj"),
            SolutionEntry("Tests", @"Tests\Tests.csproj"),
            SolutionEntry("Entities", @"Entities\Entities.csproj"),
            SolutionEntry("Shared", @"Shared\Shared.csproj"),
        ], _root);

        // Whatever survives the pre-filter, push builds — so give those two real output to reflect.
        var pluginsOutput = Path.Combine(_root, "Plugins", "bin", "Release", "net462", "publish");
        Directory.CreateDirectory(pluginsOutput);
        var pluginsDll = BuildPluginDll(pluginsOutput, "Plugins", "Acme.AccountPostCreatePlugin");
        CopyXrmSdkDllNextTo(pluginsOutput);

        var sharedOutput = Path.Combine(_root, "Shared", "bin", "Release", "net462");
        Directory.CreateDirectory(sharedOutput);
        BuildDependencyDll(sharedOutput, "Shared", "Acme.SharedHelper");

        var resolved = new List<string>();

        foreach (var candidate in candidates.Where(c => PluginProjectResolver.DescribePreFilterSkip(c.ProjectPath) == null))
        {
            var dll = PluginProjectResolver.ResolvePluginAssembly(candidate, _ => { });
            if (dll != null) resolved.Add(dll);
        }

        resolved.Should().ContainSingle().Which.Should().Be(pluginsDll);
    }

    // ---- fixtures ----

    void WriteSolution(params string[] relativeProjectPaths)
    {
        var projects = string.Concat(relativeProjectPaths.Select(p => $"""<Project Path="{p}" />"""));
        File.WriteAllText(Path.Combine(_root, "Test.slnx"), $"<Solution>{projects}</Solution>");
    }

    static MsBuildSolutionProject SolutionEntry(string name, string path) =>
        new(path.Replace('\\', Path.DirectorySeparatorChar), name, Path.GetExtension(path).ToLowerInvariant());

    static string PluginProjectXml() =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup>" +
        "<ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" Version=\"9.0.2\" />" +
        "<PackageReference Include=\"Flowline.Attributes\" Version=\"1.0.0\" /></ItemGroup></Project>";

    static string DtoProjectXml() =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup></Project>";

    string WriteProject(string folder, string fileName, string xml)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    PluginProjectCandidate Candidate(string folder, string fileName)
    {
        var projectPath = WriteProject(folder, fileName, PluginProjectXml());
        return new PluginProjectCandidate(projectPath, Path.GetFileNameWithoutExtension(fileName),
            Path.Combine(_root, folder, "bin", "Release"));
    }

    static string CreateOutput(PluginProjectCandidate candidate, params string[] subFolders)
    {
        var dir = Path.Combine([candidate.BuildOutputRoot, .. subFolders]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Minimal real assembly with one public class implementing IPlugin. Mirrors the fixture in
    // PluginAssemblyReaderTests — test projects don't share fixture code across assemblies.
    static string BuildPluginDll(string dir, string assemblyName, string pluginTypeName)
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

    // The filter matches System.Activities.CodeActivity by FullName, so a same-named local base type is
    // enough — no real System.Activities reference needed, and none is resolvable on a temp folder anyway.
    static string BuildCodeActivityDll(string dir, string assemblyName, string workflowTypeName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");

        var codeActivity = mb.DefineType("System.Activities.CodeActivity", TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();
        mb.DefineType(workflowTypeName, TypeAttributes.Public | TypeAttributes.Class, codeActivity).CreateType();

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // Stand-in for a pure-dependency DLL: no IPlugin, no CodeActivity.
    static string BuildDependencyDll(string dir, string assemblyName, string typeName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");
        mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }
    [Fact]
    public void EnumerateCandidates_MissingProjectAndNoReporter_Throws()
    {
        // push is about to build these — a solution file that lies should stop the run.
        var projects = new[] { new MsBuildSolutionProject(Path.Combine("Gone", "Gone.csproj"), "Gone", ".csproj") };

        var act = () => PluginProjectResolver.EnumerateCandidates(projects, _root);

        act.Should().Throw<FlowlineException>().Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    [Fact]
    public void EnumerateCandidates_MissingProjectWithReporter_SkipsAndReportsInsteadOfThrowing()
    {
        // deploy packs from the snapshot and never builds a plugin project, so a stale solution entry
        // for an unrelated project must not block it.
        var present = WriteProject("Real", "Real.csproj", PluginProjectXml());
        var projects = new[]
        {
            new MsBuildSolutionProject(Path.Combine("Gone", "Gone.csproj"), "Gone", ".csproj"),
            new MsBuildSolutionProject(Path.Combine("Real", "Real.csproj"), "Real", ".csproj"),
        };
        var reported = new List<string>();

        var candidates = PluginProjectResolver.EnumerateCandidates(projects, _root, reported.Add);

        candidates.Should().ContainSingle().Which.ProjectPath.Should().Be(present);
        reported.Should().ContainSingle().Which.Should().Contain("Gone");
    }


    // An IPlugin-referencing fixture assembly only reflects when Microsoft.Xrm.Sdk.dll sits where the
    // resolver looks — copy-local next to the build output, exactly as a real plugin project's is.
    static void CopyXrmSdkDllNextTo(string dir) =>
        File.Copy(
            Path.Combine(Path.GetDirectoryName(typeof(PluginProjectResolverTests).Assembly.Location)!, "Microsoft.Xrm.Sdk.dll"),
            Path.Combine(dir, "Microsoft.Xrm.Sdk.dll"),
            overwrite: true);
}
