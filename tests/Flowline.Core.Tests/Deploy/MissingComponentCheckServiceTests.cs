using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using NSubstitute;
using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Deploy;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.Deploy;

public class MissingComponentCheckServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly MissingComponentCheckService _service;
    readonly string _packagePath;

    public MissingComponentCheckServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _service = new MissingComponentCheckService(_console);

        var dir = Path.Combine(Path.GetTempPath(), $"flowline-mcc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _packagePath = Path.Combine(dir, "MySolution_1_0_0_0.zip");
        File.WriteAllBytes(_packagePath, [1, 2, 3]); // content is irrelevant — ExecuteAsync is mocked
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_packagePath)!;
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    PostDeployContext Ctx(bool includeManaged = false) =>
        new(_serviceMock,
            new DeploySolutionInfo("MySolution", "https://example.crm.dynamics.com", includeManaged, true),
            RunMode.Normal,
            _packagePath,
            Path.GetTempPath());

    static void SetUpResponse(IOrganizationServiceAsync2 serviceMock, MissingComponent[] missingComponents)
    {
        var response = new RetrieveMissingComponentsResponse
        {
            Results = new ParameterCollection { ["MissingComponents"] = missingComponents }
        };
        serviceMock.ExecuteAsync(Arg.Any<RetrieveMissingComponentsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    static MissingComponent Component(string schemaName, string displayName, string? solution, int type, string depSchema = "dep", string depDisplay = "Dependent") =>
        new()
        {
            RequiredComponent = new ComponentDetail { SchemaName = schemaName, DisplayName = displayName, Solution = solution, Type = type },
            DependentComponent = new ComponentDetail { SchemaName = depSchema, DisplayName = depDisplay }
        };

    [Fact]
    public async Task RunPreImportAsync_NoMissingComponents_DoesNotThrow()
    {
        SetUpResponse(_serviceMock, []);

        var act = async () => await _service.RunPreImportAsync(Ctx(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunPreImportAsync_SevenMissingComponents_ThrowsValidationFailed()
    {
        var missing = Enumerable.Range(1, 7)
            .Select(i => Component($"new_field{i}", $"Field {i}", "ContosoSolution", 2))
            .ToArray();
        SetUpResponse(_serviceMock, missing);

        var act = async () => await _service.RunPreImportAsync(Ctx(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<FlowlineException>();
        thrown.Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
    }

    [Fact]
    public void MapMissingComponents_SevenMissingComponents_ProducesSevenResults()
    {
        var missing = Enumerable.Range(1, 7)
            .Select(i => Component($"new_field{i}", $"Field {i}", "ContosoSolution", 2))
            .ToArray();

        var results = MissingComponentCheckService.MapMissingComponents(missing);

        results.Should().HaveCount(7);
    }

    [Fact]
    public void MapMissingComponents_PopulatedOwningSolution_SurfacesIt()
    {
        var missing = new[] { Component("new_field", "Field", "ContosoSolution", 2) };

        var results = MissingComponentCheckService.MapMissingComponents(missing);

        results.Single().RequiredSolution.Should().Be("ContosoSolution");
    }

    [Fact]
    public void MapMissingComponents_EmptyParentFieldsAndId_StillProducesSchemaAndDisplayName_NeverABareGuid()
    {
        // Mirrors the live-probe observation: RequiredComponent.Id is Guid.Empty and ParentDisplayName
        // is blank, but SchemaName/DisplayName/Solution are populated.
        var component = new MissingComponent
        {
            RequiredComponent = new ComponentDetail
            {
                SchemaName = "new_field",
                DisplayName = "Field",
                Solution = "ContosoSolution",
                Type = 2,
                Id = Guid.Empty,
                ParentDisplayName = "",
                ParentSchemaName = "",
                ParentId = Guid.Empty
            },
            DependentComponent = new ComponentDetail
            {
                SchemaName = "new_entity",
                DisplayName = "Entity",
                Solution = "" // observed: DependentComponent.Solution comes back empty
            }
        };

        var result = MissingComponentCheckService.MapMissingComponents([component]).Single();

        result.RequiredSchemaName.Should().Be("new_field");
        result.RequiredDisplayName.Should().Be("Field");
        result.DependentSchemaName.Should().Be("new_entity");
        result.DependentDisplayName.Should().Be("Entity");

        var guidText = Guid.Empty.ToString();
        result.RequiredSchemaName.Should().NotContain(guidText);
        result.DependentDisplayName.Should().NotContain(guidText);
    }

    [Fact]
    public void MapMissingComponents_FirstPartyApplicationOwner_CarriesThatApplicationAsOwningSolution()
    {
        var missing = new[] { Component("msdyn_field", "Field", "Dynamics 365 Sales", 2) };

        var results = MissingComponentCheckService.MapMissingComponents(missing);

        results.Single().RequiredSolution.Should().Be("Dynamics 365 Sales");
    }

    [Fact]
    public async Task RunPreImportAsync_RequestFaults_ThrowsConnectionFailed_NotValidationFailed_NamesTheCheck()
    {
        _serviceMock.ExecuteAsync(Arg.Any<RetrieveMissingComponentsRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<OrganizationResponse>>(_ => throw new InvalidOperationException("access denied"));

        var act = async () => await _service.RunPreImportAsync(Ctx(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<FlowlineException>();
        thrown.Which.ExitCode.Should().Be(ExitCode.ConnectionFailed);
        thrown.Which.Message.Should().Contain("check");
        thrown.Which.Message.Should().Contain("--skip-component-check");
    }

    [Fact]
    public async Task RunPreImportAsync_NeverIssuesWriteRequests()
    {
        var missing = new[] { Component("new_field", "Field", "ContosoSolution", 2) };
        SetUpResponse(_serviceMock, missing);

        try { await _service.RunPreImportAsync(Ctx(), CancellationToken.None); }
        catch (FlowlineException) { /* expected — blocked deploy */ }

        await _serviceMock.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName != "RetrieveMissingComponents")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunPreImportAsync_ManagedAndUnmanagedContext_BlockIdentically(bool includeManaged)
    {
        var missing = new[] { Component("new_field", "Field", "ContosoSolution", 2) };
        SetUpResponse(_serviceMock, missing);

        var act = async () => await _service.RunPreImportAsync(Ctx(includeManaged), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<FlowlineException>();
        thrown.Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
    }

    [Fact]
    public void MapMissingComponents_Null_ReturnsEmptyList()
    {
        var results = MissingComponentCheckService.MapMissingComponents(null);

        results.Should().BeEmpty();
    }

    // FIX 1 end-to-end: a clean run must remove a report left behind by an earlier blocked run
    // against this same target — the file's presence always describes the latest outcome.
    [Fact]
    public async Task RunPreImportAsync_CleanRun_RemovesPreExistingReportForThatTarget()
    {
        var ctx = Ctx();
        var reportPath = MissingComponentReport.GetReportPath(ctx.PackagePath, ctx.Solution.EnvironmentUrl);
        File.WriteAllText(reportPath, "stale report from an earlier blocked run");
        SetUpResponse(_serviceMock, []);

        await _service.RunPreImportAsync(ctx, CancellationToken.None);

        File.Exists(reportPath).Should().BeFalse();
    }

    [Fact]
    public async Task RunPreImportAsync_BlockedRun_LeavesReportOnDiskWithExpectedEntries()
    {
        var ctx = Ctx();
        var missing = new[] { Component("new_field", "Field", "ContosoSolution", 2) };
        SetUpResponse(_serviceMock, missing);

        try { await _service.RunPreImportAsync(ctx, CancellationToken.None); }
        catch (FlowlineException) { /* expected — blocked deploy */ }

        var reportPath = MissingComponentReport.GetReportPath(ctx.PackagePath, ctx.Solution.EnvironmentUrl);
        File.Exists(reportPath).Should().BeTrue();
        var content = File.ReadAllText(reportPath);
        content.Should().Contain("new_field");
        content.Should().Contain(ctx.Solution.Name);
        content.Should().Contain(ctx.Solution.EnvironmentUrl);
    }

    // FIX C: a privilege fault is a permanent problem, not a transport hiccup — a script retrying on
    // ConnectionFailed would loop forever against it, so it needs its own ExitCode and wording.
    [Fact]
    public async Task RunPreImportAsync_PrivilegeDeniedFault_ThrowsNotAuthenticated_WithDistinctMessage()
    {
        _serviceMock.ExecuteAsync(Arg.Any<RetrieveMissingComponentsRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<OrganizationResponse>>(_ => throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040220) }, "Principal user is missing prvReadSolution privilege"));

        var act = async () => await _service.RunPreImportAsync(Ctx(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<FlowlineException>();
        thrown.Which.ExitCode.Should().Be(ExitCode.NotAuthenticated);
        thrown.Which.Message.Should().Contain("privilege");
        thrown.Which.Message.Should().Contain("--skip-component-check");
        thrown.Which.Message.Should().NotBe(MissingComponentCheckService.BuildConnectionFailedMessage("Principal user is missing prvReadSolution privilege", 0));
    }

    // A fault that isn't a privilege problem must keep going through the transport-failure path —
    // only the privilege family gets the NotAuthenticated treatment.
    [Fact]
    public async Task RunPreImportAsync_NonPrivilegeFault_StillThrowsConnectionFailed()
    {
        _serviceMock.ExecuteAsync(Arg.Any<RetrieveMissingComponentsRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<OrganizationResponse>>(_ => throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040265) }, "Generic SQL error"));

        var act = async () => await _service.RunPreImportAsync(Ctx(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<FlowlineException>();
        thrown.Which.ExitCode.Should().Be(ExitCode.ConnectionFailed);
    }

    [Fact]
    public void IsPrivilegeFault_PrivilegeDeniedErrorCode_ReturnsTrue()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040220) }, "denied");

        MissingComponentCheckService.IsPrivilegeFault(fault).Should().BeTrue();
    }

    [Fact]
    public void IsPrivilegeFault_AccessDeniedMessageText_ReturnsTrue()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault(), "Access is Denied");

        MissingComponentCheckService.IsPrivilegeFault(fault).Should().BeTrue();
    }

    [Fact]
    public void IsPrivilegeFault_UnrelatedFault_ReturnsFalse()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040265) }, "Generic SQL error");

        MissingComponentCheckService.IsPrivilegeFault(fault).Should().BeFalse();
    }

    // FIX B: the ceiling is unmeasured, so the size line must only appear once it's plausibly relevant —
    // a small payload that fails is a real connectivity/auth problem, not a transport-size issue.
    [Fact]
    public void BuildConnectionFailedMessage_LargePayload_NamesTheSize()
    {
        var message = MissingComponentCheckService.BuildConnectionFailedMessage("timed out", 40L * 1024 * 1024);

        message.Should().Contain("MB");
        message.Should().Contain("--skip-component-check");
    }

    [Fact]
    public void BuildConnectionFailedMessage_SmallPayload_DoesNotMentionSize()
    {
        var message = MissingComponentCheckService.BuildConnectionFailedMessage("timed out", 1024);

        message.Should().NotContain("MB");
        message.Should().Contain("--skip-component-check");
    }
}
