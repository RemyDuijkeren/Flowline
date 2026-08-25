using Flowline.Core.Deploy;
using Flowline.Core.Models;
using Flowline.Core.Services;
using FluentAssertions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Spectre.Console.Testing;
using static Flowline.Core.Tests.PluginDllFixtures;

namespace Flowline.Core.Tests.Deploy;

// Pre-import repair: creates the pluginassembly record Dataverse doesn't create for an assembly added
// to a package the target already holds. Fixture shape mirrors PluginPackageAssemblyCheckServiceTests —
// same unpack tree, same real-DLL reflection, since both walk the same package content.
public class PluginPackageAssemblyRepairServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly PluginPackageAssemblyRepairService _service;
    readonly List<string> _tempDirs = [];

    public PluginPackageAssemblyRepairServiceTests()
    {
        // Reflection is memoized per package directory for the whole process, and xUnit parallelizes
        // classes — without this a rebuilt fixture can read a previous case's result.
        Flowline.Core.Plugins.PluginPackageContentReader.ClearCache();

        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting assertion substrings across lines

        _service = new PluginPackageAssemblyRepairService(_console);

        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
        // The registrar never reads the response, but awaiting an unstubbed Task would throw.
        _serviceMock.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<OrganizationResponse>(new CreateResponse()));
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

    PostDeployContext Ctx(string unpackRoot, bool managed = false, RunMode mode = RunMode.Normal) =>
        new(_serviceMock,
            new DeploySolutionInfo("MySolution", "https://example.crm.dynamics.com", managed, true),
            mode,
            Path.Combine(unpackRoot, "unused.zip"),
            unpackRoot);

    string BuildUnpackTree(params (string UniqueName, string? NupkgPath)[] packages)
    {
        var root = NewTempDir("flowline-pkgrepair-unpack-");
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

    // One package, one plugin-bearing assembly named MyPlugins, laid out as pac solution unpack writes it.
    string BuildSinglePluginPackage(string uniqueName)
    {
        var buildDir = NewTempDir("flowline-pkgrepair-build-");
        var dll = BuildPluginDll(buildDir, "MyPlugins", "MyPlugins.FirstPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        return BuildUnpackTree((uniqueName, nupkg));
    }

    void SetUpPackageFound(string uniqueName, Guid packageId) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q!.EntityName == "pluginpackage" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "uniquename" && (string)c.Values[0] == uniqueName)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("pluginpackage", packageId)])));

    void SetUpAssemblyFound(Guid packageId, string assemblyName) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q!.EntityName == "pluginassembly" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && (Guid)c.Values[0] == packageId) &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "name" && (string)c.Values[0] == assemblyName)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("pluginassembly", Guid.NewGuid())])));

    List<CreateRequest> CreateRequests() =>
        _serviceMock.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IOrganizationServiceAsync2.ExecuteAsync))
            .Select(c => c.GetArguments()[0])
            .OfType<CreateRequest>()
            .ToList();

    // ---- tests ----

    [Fact]
    public async Task RunPreImportAsync_AssemblyCarriedInContentWithNoRecord_CreatesItUnderThePackage()
    {
        var root = BuildSinglePluginPackage("av_MyPackage");
        var packageId = Guid.NewGuid();
        SetUpPackageFound("av_MyPackage", packageId);

        await _service.RunPreImportAsync(Ctx(root), CancellationToken.None);

        var created = CreateRequests();
        created.Should().HaveCount(1);

        var entity = created[0].Target;
        entity.LogicalName.Should().Be("pluginassembly");
        entity["name"].Should().Be("MyPlugins");
        ((EntityReference)entity["packageid"]).Id.Should().Be(packageId);
        ((OptionSetValue)entity["isolationmode"]).Value.Should().Be(2); // sandbox — a package assembly can't be full-trust
        // Identity is what Dataverse resolves the DLL by: a package-owned row carries no content of its own.
        entity.Contains("version").Should().BeTrue();
        entity.Contains("culture").Should().BeTrue();
        // Without this the record lands outside the solution being deployed.
        created[0]["SolutionUniqueName"].Should().Be("MySolution");

        _console.Output.Should().Contain("MyPlugins").And.Contain("created it");
    }

    // Negative control: a service that created unconditionally passes every other test in this file.
    [Fact]
    public async Task RunPreImportAsync_EveryAssemblyAlreadyRegistered_CreatesNothingAndSaysNothing()
    {
        var root = BuildSinglePluginPackage("av_MyPackage");
        var packageId = Guid.NewGuid();
        SetUpPackageFound("av_MyPackage", packageId);
        SetUpAssemblyFound(packageId, "MyPlugins");

        await _service.RunPreImportAsync(Ctx(root), CancellationToken.None);

        CreateRequests().Should().BeEmpty();
        _console.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task RunPreImportAsync_TargetDoesNotHoldThePackage_CreatesNothing()
    {
        // No SetUpPackageFound: the default empty result stands in for a first import, where the import
        // creates the package and registers its content itself.
        var root = BuildSinglePluginPackage("av_MyPackage");

        await _service.RunPreImportAsync(Ctx(root), CancellationToken.None);

        CreateRequests().Should().BeEmpty();
        _console.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task RunPreImportAsync_ManagedTarget_WritesNothingButStillReportsTheMissingRecord()
    {
        var root = BuildSinglePluginPackage("av_MyPackage");
        SetUpPackageFound("av_MyPackage", Guid.NewGuid());

        await _service.RunPreImportAsync(Ctx(root, managed: true), CancellationToken.None);

        // Both halves matter. Silence would leave a managed import failing on a bound step with nothing
        // in Flowline's output naming the cause — the post-import check never runs when the import fails.
        CreateRequests().Should().BeEmpty();
        _console.Output.Should().Contain("MyPlugins").And.Contain("no registration in the target");
        _console.Output.Should().Contain("Fix it:");
    }

    // NoDelete is named for deletes, but it reads as "don't touch my target" and it must suppress this
    // create too. Asserted rather than left to the flag's name, so a later reading of that name doesn't
    // turn --no-delete into a silently mutating run.
    [Theory]
    [InlineData(RunMode.DryRun)]
    [InlineData(RunMode.NoDelete)]
    public async Task RunPreImportAsync_ReportOnlyRunMode_WritesNothingAndSaysWhatItWouldRegister(RunMode mode)
    {
        var root = BuildSinglePluginPackage("av_MyPackage");
        SetUpPackageFound("av_MyPackage", Guid.NewGuid());

        await _service.RunPreImportAsync(Ctx(root, mode: mode), CancellationToken.None);

        CreateRequests().Should().BeEmpty();
        _console.Output.Should().Contain("MyPlugins").And.Contain("would create");
    }

    [Fact]
    public async Task RunPreImportAsync_CreateFaults_WarnsAndDoesNotThrow()
    {
        var root = BuildSinglePluginPackage("av_MyPackage");
        SetUpPackageFound("av_MyPackage", Guid.NewGuid());
        _serviceMock.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<OrganizationResponse>>(_ => throw new InvalidOperationException("privilege missing"));

        var act = () => _service.RunPreImportAsync(Ctx(root), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _console.Output.Should().Contain("privilege missing").And.Contain("Importing anyway");
    }

    [Fact]
    public async Task RunPreImportAsync_PackageLookupFaults_WarnsAndDoesNotThrow()
    {
        var root = BuildSinglePluginPackage("av_MyPackage");
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q!.EntityName == "pluginpackage"),
                Arg.Any<CancellationToken>())
            .Returns<Task<EntityCollection>>(_ => throw new InvalidOperationException("connection reset"));

        var act = () => _service.RunPreImportAsync(Ctx(root), CancellationToken.None);

        await act.Should().NotThrowAsync();
        CreateRequests().Should().BeEmpty();
        _console.Output.Should().Contain("Importing anyway");
    }

    [Fact]
    public async Task RunPreImportAsync_SolutionCarriesNoPluginPackages_DoesNothing()
    {
        var root = NewTempDir("flowline-pkgrepair-empty-");

        await _service.RunPreImportAsync(Ctx(root), CancellationToken.None);

        CreateRequests().Should().BeEmpty();
        _console.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task RunPostImportAsync_IsANoOp_TheCheckServiceOwnsThePostImportVerdict()
    {
        var root = BuildSinglePluginPackage("av_MyPackage");
        SetUpPackageFound("av_MyPackage", Guid.NewGuid());

        var outcome = await _service.RunPostImportAsync(Ctx(root), CancellationToken.None);

        outcome.Should().Be(PostDeployOutcome.Clean);
        CreateRequests().Should().BeEmpty();
        _console.Output.Should().BeEmpty();
    }
}
