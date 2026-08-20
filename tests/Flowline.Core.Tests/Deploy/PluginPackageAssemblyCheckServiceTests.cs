using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using Flowline.Core;
using Flowline.Core.Deploy;
using Flowline.Core.Models;
using Flowline.Core.Services;
using FluentAssertions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.Deploy;

public class PluginPackageAssemblyCheckServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly PluginPackageAssemblyCheckService _service;
    readonly List<string> _tempDirs = [];

    public PluginPackageAssemblyCheckServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting assertion substrings across lines
        _service = new PluginPackageAssemblyCheckService(_console)
        {
            // Retry scenarios would otherwise pay real seconds per attempt (PluginService follows the
            // same instance-property pattern for the same reason).
            PollMaxAttempts = 3,
            PollDelay = TimeSpan.Zero
        };

        // Default empty result for any query not explicitly set up below.
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
    }

    string NewTempDir(string prefix)
    {
        var dir = Directory.CreateTempSubdirectory(prefix).FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    PostDeployContext Ctx(string unpackRoot) =>
        new(_serviceMock,
            new DeploySolutionInfo("MySolution", "https://example.crm.dynamics.com", false, true),
            RunMode.Normal,
            Path.Combine(unpackRoot, "unused.zip"),
            unpackRoot);

    // ---- unpack-tree fixtures ----

    // Lays out pluginpackages/<uniqueName>/pluginpackage.xml (+ package/<nupkg>.nupkg when supplied),
    // mirroring the pac solution unpack shape KTD2 verified against a real export.
    string BuildUnpackTree(params (string UniqueName, string? NupkgPath)[] packages)
    {
        var root = NewTempDir("flowline-pkgcheck-unpack-");
        var packagesRoot = Path.Combine(root, "pluginpackages");
        foreach (var (uniqueName, nupkgPath) in packages)
        {
            var packageDir = Directory.CreateDirectory(Path.Combine(packagesRoot, uniqueName)).FullName;
            File.WriteAllText(Path.Combine(packageDir, "pluginpackage.xml"),
                $"""<pluginpackage uniquename="{uniqueName}"><name>{uniqueName}</name></pluginpackage>""");

            if (nupkgPath == null) continue;
            var packageContentDir = Directory.CreateDirectory(Path.Combine(packageDir, "package")).FullName;
            File.Copy(nupkgPath, Path.Combine(packageContentDir, Path.GetFileName(nupkgPath)));
        }
        return root;
    }

    // ---- real-assembly fixtures (mirrors PluginAssemblyReaderTests — AnalyzePackage reflects genuine
    // DLLs on disk, so an in-memory mock type won't do) ----

    static string BuildPluginDll(string dir, string assemblyName, string pluginTypeName, string? workflowTypeName = null)
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

        if (workflowTypeName != null)
        {
            var codeActivityType = mb.DefineType("System.Activities.CodeActivity", TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();
            mb.DefineType(workflowTypeName, TypeAttributes.Public | TypeAttributes.Class, codeActivityType).CreateType();
        }

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    static string BuildDependencyDll(string dir, string assemblyName, string typeName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");
        mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    static string BuildNupkg(string dir, params string[] dllPaths)
    {
        var nupkgPath = Path.Combine(dir, $"{Guid.NewGuid():N}.nupkg");
        using var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);
        foreach (var dllPath in dllPaths)
            archive.CreateEntryFromFile(dllPath, $"lib/net10.0/{Path.GetFileName(dllPath)}");
        return nupkgPath;
    }

    // ---- Dataverse query stubs ----

    void SetUpPackageFound(string uniqueName, Guid packageId) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "uniquename" && (string)c.Values[0] == uniqueName))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("pluginpackage", packageId)])));

    void SetUpPackageMissing(string uniqueName) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "uniquename" && (string)c.Values[0] == uniqueName))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

    void SetUpPackageLookupThrows(string uniqueName) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "uniquename" && (string)c.Values[0] == uniqueName))),
                Arg.Any<CancellationToken>())
            .Returns<Task<EntityCollection>>(_ => throw new InvalidOperationException("connection reset"));

    // Returns the assembly's id so callers can also stub its plugin-type query (SetUpPluginTypesFound) —
    // Fix B's zero-plugin-types check runs for every assembly this makes findable, so a test that wants
    // a fully clean result must stub both.
    Guid SetUpAssemblyFound(Guid packageId, string assemblyName)
    {
        var assemblyId = Guid.NewGuid();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && (Guid)c.Values[0] == packageId) &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "name" && (string)c.Values[0] == assemblyName))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("pluginassembly", assemblyId)])));
        return assemblyId;
    }

    // Fix B: without this, the default empty-result stub answers the plugintype query too, and the
    // assembly counts as a zero-plugin-types finding — tests that want a genuinely clean assembly need
    // this stubbed alongside SetUpAssemblyFound.
    void SetUpPluginTypesFound(Guid assemblyId, int count = 1) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "plugintype" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "pluginassemblyid" && (Guid)c.Values[0] == assemblyId))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(
                Enumerable.Range(0, count)
                    .Select(i => new Entity("plugintype", Guid.NewGuid()) { ["typename"] = $"Type{i}" })
                    .ToList())));

    // Absent on every call — the default empty-result stub already covers this, kept explicit for
    // readability at call sites that rely on it.
    void SetUpAssemblyNeverFound(Guid packageId, string assemblyName) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && (Guid)c.Values[0] == packageId) &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "name" && (string)c.Values[0] == assemblyName))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

    Guid SetUpAssemblyFoundOnSecondCall(Guid packageId, string assemblyName)
    {
        var assemblyId = Guid.NewGuid();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && (Guid)c.Values[0] == packageId) &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "name" && (string)c.Values[0] == assemblyName))),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new EntityCollection()),
                Task.FromResult(new EntityCollection([new Entity("pluginassembly", assemblyId)])));
        return assemblyId;
    }

    void SetUpAssemblyLookupThrows(Guid packageId, string assemblyName) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && (Guid)c.Values[0] == packageId) &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "name" && (string)c.Values[0] == assemblyName))),
                Arg.Any<CancellationToken>())
            .Returns<Task<EntityCollection>>(_ => throw new InvalidOperationException("connection reset"));

    // ---- tests ----

    [Fact]
    public async Task RunPostImportAsync_PackageHasTwoAssembliesTargetHoldsOne_ReturnsOneAndWarnsWithRemedy()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var registeredDll = BuildPluginDll(buildDir, "RegisteredAssembly", "RegisteredPlugin");
        var missingDll = BuildPluginDll(buildDir, "MissingAssembly", "MissingPlugin");
        var nupkg = BuildNupkg(buildDir, registeredDll, missingDll);
        var unpackRoot = BuildUnpackTree(("abc_TestPackage", nupkg));

        var packageId = Guid.NewGuid();
        SetUpPackageFound("abc_TestPackage", packageId);
        var registeredId = SetUpAssemblyFound(packageId, "RegisteredAssembly");
        SetUpPluginTypesFound(registeredId);
        SetUpAssemblyNeverFound(packageId, "MissingAssembly");

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(1);
        result.Inconclusive.Should().BeFalse();
        // FIX A: this is the concrete value DeployCommand.ResolvePostImportExitCode keys off to prefer
        // 21 over 18 — proving it here, not just in the resolver's own unit tests, catches the case
        // where this service stops setting it.
        result.PreferredExitCode.Should().Be(ExitCode.AssemblyNotRegistered);
        var output = _console.Output;
        output.Should().Contain("MissingAssembly");
        output.Should().Contain("abc_TestPackage");
        output.Should().Contain("will not run");
        output.Should().Contain("isolationmode sandbox");
        output.Should().Contain("repeat on every later deploy");
        output.Should().NotContain("RegisteredAssembly");
    }

    [Fact]
    public async Task RunPostImportAsync_PackageHasTwoAssembliesTargetHoldsBoth_ReturnsZeroAndPrintsOnlyVerdict()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dllOne = BuildPluginDll(buildDir, "AssemblyOne", "PluginOne");
        var dllTwo = BuildPluginDll(buildDir, "AssemblyTwo", "PluginTwo");
        var nupkg = BuildNupkg(buildDir, dllOne, dllTwo);
        var unpackRoot = BuildUnpackTree(("abc_TestPackage", nupkg));

        var packageId = Guid.NewGuid();
        SetUpPackageFound("abc_TestPackage", packageId);
        var oneId = SetUpAssemblyFound(packageId, "AssemblyOne");
        SetUpPluginTypesFound(oneId);
        var twoId = SetUpAssemblyFound(packageId, "AssemblyTwo");
        SetUpPluginTypesFound(twoId);

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeFalse();
        // Proves the discarding console actually suppresses AnalyzePackage's own "analyzed" lines —
        // a plain "contains the verdict" assertion would pass even if that leaked.
        _console.Output.Should().NotContain("analyzed");
        _console.Lines.Should().ContainSingle();
        _console.Output.Should().Contain("registered");
    }

    [Fact]
    public async Task RunPostImportAsync_AssemblyAbsentThenPresent_ReturnsZeroNoFindingNoWarning()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dll = BuildPluginDll(buildDir, "SlowAssembly", "SlowPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var unpackRoot = BuildUnpackTree(("abc_TestPackage", nupkg));

        var packageId = Guid.NewGuid();
        SetUpPackageFound("abc_TestPackage", packageId);
        var slowId = SetUpAssemblyFoundOnSecondCall(packageId, "SlowAssembly");
        SetUpPluginTypesFound(slowId);

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeFalse();
        _console.Output.Should().NotContain("!"); // no warning glyph
    }

    [Fact]
    public async Task RunPostImportAsync_AssemblyAbsentOnEveryAttempt_ReturnsOne()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dll = BuildPluginDll(buildDir, "NeverAssembly", "NeverPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var unpackRoot = BuildUnpackTree(("abc_TestPackage", nupkg));

        var packageId = Guid.NewGuid();
        SetUpPackageFound("abc_TestPackage", packageId);
        SetUpAssemblyNeverFound(packageId, "NeverAssembly");

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(1);
        result.Inconclusive.Should().BeFalse();
    }

    [Fact]
    public async Task RunPostImportAsync_NoPluginPackagesDirectory_ReturnsZeroPrintsNothingNoDataverseCall()
    {
        var unpackRoot = NewTempDir("flowline-pkgcheck-unpack-"); // no pluginpackages/ subfolder at all

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        // Fix C/D: the solution genuinely has no plug-in package — stays silent, not inconclusive.
        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeFalse();
        _console.Output.Should().BeEmpty();
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPostImportAsync_TargetHasNoMatchingPackage_ReturnsZeroPrintsNothing()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dll = BuildPluginDll(buildDir, "OrphanedAssembly", "OrphanedPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var unpackRoot = BuildUnpackTree(("abc_GoneFromTarget", nupkg));

        SetUpPackageMissing("abc_GoneFromTarget");

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        // R6: target doesn't hold this package at all — no finding, and not inconclusive either.
        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeFalse();
        _console.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task RunPostImportAsync_TwoPackagesOneCleanOneMissing_ReturnsOneWarnsOnlySecond()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var cleanDll = BuildPluginDll(buildDir, "CleanAssembly", "CleanPlugin");
        var cleanNupkg = BuildNupkg(buildDir, cleanDll);
        var brokenDll = BuildPluginDll(buildDir, "BrokenAssembly", "BrokenPlugin");
        var brokenNupkg = BuildNupkg(buildDir, brokenDll);
        var unpackRoot = BuildUnpackTree(
            ("abc_CleanPackage", cleanNupkg),
            ("abc_BrokenPackage", brokenNupkg));

        var cleanPackageId = Guid.NewGuid();
        var brokenPackageId = Guid.NewGuid();
        SetUpPackageFound("abc_CleanPackage", cleanPackageId);
        var cleanId = SetUpAssemblyFound(cleanPackageId, "CleanAssembly");
        SetUpPluginTypesFound(cleanId);
        SetUpPackageFound("abc_BrokenPackage", brokenPackageId);
        SetUpAssemblyNeverFound(brokenPackageId, "BrokenAssembly");

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(1);
        result.Inconclusive.Should().BeFalse();
        _console.Output.Should().Contain("BrokenAssembly");
        _console.Output.Should().NotContain("CleanAssembly");
    }

    [Fact]
    public async Task RunPostImportAsync_TwoPackagesOneCleanOneHitsWarnPath_WarnsAndPrintsNoCleanVerdict()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var cleanDll = BuildPluginDll(buildDir, "CleanAssembly2", "CleanPlugin2");
        var cleanNupkg = BuildNupkg(buildDir, cleanDll);
        var unpackRoot = BuildUnpackTree(
            ("abc_CleanPackage2", cleanNupkg),
            ("abc_NoNupkgPackage", null)); // no .nupkg under package/ — R7 warn path

        var cleanPackageId = Guid.NewGuid();
        SetUpPackageFound("abc_CleanPackage2", cleanPackageId);
        var cleanId = SetUpAssemblyFound(cleanPackageId, "CleanAssembly2");
        SetUpPluginTypesFound(cleanId);

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        // Fix C: the sibling package's missing .nupkg means this pass couldn't verify everything.
        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeTrue();
        _console.Output.Should().Contain("abc_NoNupkgPackage");
        _console.Output.Should().NotContain("registered.");
    }

    [Fact]
    public async Task RunPostImportAsync_PackageReflectsToZeroPluginBearingAssemblies_NoCleanVerdictLine()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dependencyDll = BuildDependencyDll(buildDir, "PureDependency", "SomeHelper");
        var nupkg = BuildNupkg(buildDir, dependencyDll);
        var unpackRoot = BuildUnpackTree(("abc_DependencyOnlyPackage", nupkg));

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        // Fix C: DLLs were present in the nupkg, just none plugin-bearing — a genuine dependency-only
        // package. That's a normal, silent result, not "couldn't verify".
        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeFalse();
        _console.Output.Should().NotContain("registered.");
        // No package lookup either — nothing plugin-bearing was reflected to check against the target.
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(
            Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage")),
            Arg.Any<CancellationToken>());
    }

    // Fix C: distinguishes this from the dependency-only case above — the nupkg itself has no DLLs at
    // all, so nothing was readable, and that's inconclusive rather than a silent clean pass.
    [Fact]
    public async Task RunPostImportAsync_NupkgHasNoDllsAtAll_Inconclusive()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var nupkg = BuildNupkg(buildDir); // zero DLLs packed
        var unpackRoot = BuildUnpackTree(("abc_EmptyNupkgPackage", nupkg));

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeTrue();
        _console.Output.Should().NotContain("registered.");
    }

    [Fact]
    public async Task RunPostImportAsync_NupkgMissingFromPackageDirectory_WarnsNamesPackageReturnsZeroDoesNotThrow()
    {
        var unpackRoot = BuildUnpackTree(("abc_NoNupkg", null));

        var act = async () => await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.Findings.Should().Be(0);
        result.Which.Inconclusive.Should().BeTrue();
        _console.Output.Should().Contain("abc_NoNupkg");
    }

    [Fact]
    public async Task RunPostImportAsync_AnalyzePackageThrows_WarnsNamesPackageAndReasonReturnsZeroDoesNotThrow()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var workflowDll = BuildPluginDll(buildDir, "WorkflowAssembly", "PackagePlugin", workflowTypeName: "PackageWorkflowActivity");
        var nupkg = BuildNupkg(buildDir, workflowDll);
        var unpackRoot = BuildUnpackTree(("abc_WorkflowPackage", nupkg));

        var act = async () => await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.Findings.Should().Be(0);
        result.Which.Inconclusive.Should().BeTrue();
        _console.Output.Should().Contain("abc_WorkflowPackage");
        _console.Output.Should().Contain("workflow activity");
    }

    [Fact]
    public async Task RunPostImportAsync_PluginPackageLookupThrows_WarnsReturnsZeroDoesNotThrow()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dll = BuildPluginDll(buildDir, "LookupThrowsAssembly", "LookupThrowsPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var unpackRoot = BuildUnpackTree(("abc_LookupThrowsPackage", nupkg));

        SetUpPackageLookupThrows("abc_LookupThrowsPackage");

        var act = async () => await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.Findings.Should().Be(0);
        result.Which.Inconclusive.Should().BeTrue();
        _console.Output.Should().Contain("abc_LookupThrowsPackage");
    }

    [Fact]
    public async Task RunPostImportAsync_FindPackageAssemblyThrows_WarnsReturnsZeroDoesNotThrow()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dll = BuildPluginDll(buildDir, "AssemblyLookupThrows", "AssemblyLookupThrowsPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var unpackRoot = BuildUnpackTree(("abc_AssemblyLookupThrowsPackage", nupkg));

        var packageId = Guid.NewGuid();
        SetUpPackageFound("abc_AssemblyLookupThrowsPackage", packageId);
        SetUpAssemblyLookupThrows(packageId, "AssemblyLookupThrows");

        var act = async () => await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.Findings.Should().Be(0);
        result.Which.Inconclusive.Should().BeTrue();
        _console.Output.Should().Contain("abc_AssemblyLookupThrowsPackage");
    }

    [Fact]
    public async Task RunPostImportAsync_PlainDependencyDllAlongsidePluginDll_ContributesNoFindingForIt()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var pluginDll = BuildPluginDll(buildDir, "RealPlugin", "RealPluginType");
        var dependencyDll = BuildDependencyDll(buildDir, "Newtonsoft.Json.Fake", "JsonHelper");
        var nupkg = BuildNupkg(buildDir, pluginDll, dependencyDll);
        var unpackRoot = BuildUnpackTree(("abc_MixedPackage", nupkg));

        var packageId = Guid.NewGuid();
        SetUpPackageFound("abc_MixedPackage", packageId);
        var realId = SetUpAssemblyFound(packageId, "RealPlugin");
        SetUpPluginTypesFound(realId);

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(0);
        result.Inconclusive.Should().BeFalse();
        _console.Output.Should().NotContain("Newtonsoft");
    }

    // Fix B: a pluginassembly row can exist under the package and still carry zero plugin types — the
    // exact dead-end state the documented remedy passes through when the import writes no content.
    [Fact]
    public async Task RunPostImportAsync_AssemblyRegisteredWithZeroPluginTypes_ReturnsOneFindingWithDistinctWording()
    {
        var buildDir = NewTempDir("flowline-pkgcheck-build-");
        var dll = BuildPluginDll(buildDir, "EmptyTypesAssembly", "EmptyTypesPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var unpackRoot = BuildUnpackTree(("abc_EmptyTypesPackage", nupkg));

        var packageId = Guid.NewGuid();
        SetUpPackageFound("abc_EmptyTypesPackage", packageId);
        SetUpAssemblyFound(packageId, "EmptyTypesAssembly"); // plugintype query left unstubbed — default empty

        var result = await _service.RunPostImportAsync(Ctx(unpackRoot), CancellationToken.None);

        result.Findings.Should().Be(1);
        result.Inconclusive.Should().BeFalse();
        result.PreferredExitCode.Should().Be(ExitCode.AssemblyNotRegistered);
        _console.Output.Should().Contain("EmptyTypesAssembly");
        _console.Output.Should().Contain("no plugin types");
        _console.Output.Should().Contain("repeat on every later deploy");
    }

    [Fact]
    public async Task RunPreImportAsync_PerformsNoDataverseCallAndPrintsNothing()
    {
        var unpackRoot = NewTempDir("flowline-pkgcheck-unpack-");

        await _service.RunPreImportAsync(Ctx(unpackRoot), CancellationToken.None);

        _console.Output.Should().BeEmpty();
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }
}
