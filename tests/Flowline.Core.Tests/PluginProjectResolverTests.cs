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
        WriteProject("Package", "Package.cdsproj", "<Project />");

        var candidates = PluginProjectResolver.EnumerateCandidates(
        [
            SolutionEntry("Plugins", @"Plugins\Plugins.csproj"),
            SolutionEntry("Entities", @"Entities\Entities.csproj"),
            SolutionEntry("Package", @"Package\Package.cdsproj"),
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
           .Which.Message.Should().Contain("build it first").And.Contain("Plugins");
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

    // ---- fixtures ----

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

    // An IPlugin-referencing fixture assembly only reflects when Microsoft.Xrm.Sdk.dll sits where the
    // resolver looks — copy-local next to the build output, exactly as a real plugin project's is.
    static void CopyXrmSdkDllNextTo(string dir) =>
        File.Copy(
            Path.Combine(Path.GetDirectoryName(typeof(PluginProjectResolverTests).Assembly.Location)!, "Microsoft.Xrm.Sdk.dll"),
            Path.Combine(dir, "Microsoft.Xrm.Sdk.dll"),
            overwrite: true);
}
