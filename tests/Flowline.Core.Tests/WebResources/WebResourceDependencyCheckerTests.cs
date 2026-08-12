using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.WebResources;
using FluentAssertions;

namespace Flowline.Core.Tests.WebResources;

public class WebResourceDependencyCheckerTests
{
    readonly IOrganizationServiceAsync2 _serviceMock = Substitute.For<IOrganizationServiceAsync2>();

    static Entity DependencyRecord(int type, Guid objectId, string? formattedLabel = null)
    {
        var entity = new Entity("dependency")
        {
            ["dependentcomponenttype"] = new OptionSetValue(type),
            ["dependentcomponentobjectid"] = objectId
        };
        if (formattedLabel is not null)
            entity.FormattedValues["dependentcomponenttype"] = formattedLabel;
        return entity;
    }

    void SetupDependencies(Guid webResourceId, params Entity[] dependents) =>
        _serviceMock.ExecuteAsync(
                Arg.Is<RetrieveDependenciesForDeleteRequest>(r => r!.ObjectId == webResourceId),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(new RetrieveDependenciesForDeleteResponse
            {
                Results = { ["EntityCollection"] = new EntityCollection(dependents.ToList()) }
            }));

    [Fact]
    public async Task CheckAsync_SystemFormDependent_ReturnsTypeLabelAndName()
    {
        var webResourceId = Guid.NewGuid();
        var formId = Guid.NewGuid();
        SetupDependencies(webResourceId, DependencyRecord(60, formId, "Form"));
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q!.EntityName == "systemform"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("systemform", formId) { ["name"] = "Account Main Form" }
            ])));

        var results = await WebResourceDependencyChecker.CheckAsync(_serviceMock, [webResourceId]);

        var result = Assert.Single(results);
        result.Checked.Should().BeTrue();
        var dependent = Assert.Single(result.Dependents!);
        dependent.TypeLabel.Should().Be("Form");
        dependent.Name.Should().Be("Account Main Form");
        dependent.ObjectId.Should().Be(formId);
    }

    [Fact]
    public async Task CheckAsync_RibbonDependent_ReturnsRibbonTypeLabelAndObjectIdWithNoName()
    {
        var webResourceId = Guid.NewGuid();
        var ribbonObjectId = Guid.NewGuid();
        // Ribbon component types (48/49/50/52/53/55) have no nameable backing table — no
        // NameResolvableTypes entry — so Name must land on the label-and-id fallback regardless of the
        // FormattedValues label Dataverse supplies for the type itself.
        SetupDependencies(webResourceId, DependencyRecord(48, ribbonObjectId, "Ribbon Diff"));

        var results = await WebResourceDependencyChecker.CheckAsync(_serviceMock, [webResourceId]);

        var dependent = Assert.Single(Assert.Single(results).Dependents!);
        dependent.TypeLabel.Should().Be("Ribbon Diff");
        dependent.Name.Should().BeNull();
        dependent.ObjectId.Should().Be(ribbonObjectId);
    }

    // KTD4: the ManualTypeLabels fallback is load-bearing — without it, a dependent whose response
    // entity carries no FormattedValues label renders as a bare component-type number.
    [Fact]
    public async Task CheckAsync_NoFormattedValueButTypeInManualTypeLabels_ReturnsManualLabel()
    {
        var webResourceId = Guid.NewGuid();
        var siteMapObjectId = Guid.NewGuid();
        SetupDependencies(webResourceId, DependencyRecord(62, siteMapObjectId)); // no formattedLabel
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q!.EntityName == "sitemap"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var results = await WebResourceDependencyChecker.CheckAsync(_serviceMock, [webResourceId]);

        var dependent = Assert.Single(Assert.Single(results).Dependents!);
        dependent.TypeLabel.Should().Be("SiteMap");
    }

    [Fact]
    public async Task CheckAsync_NoDependents_ReturnsEmptyListDistinctFromUnchecked()
    {
        var webResourceId = Guid.NewGuid();
        SetupDependencies(webResourceId);

        var results = await WebResourceDependencyChecker.CheckAsync(_serviceMock, [webResourceId]);

        var result = Assert.Single(results);
        result.Checked.Should().BeTrue();
        result.Dependents.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_OneResourceFaults_OthersStillReturnDependents()
    {
        var faultingId = Guid.NewGuid();
        var okId = Guid.NewGuid();
        var formId = Guid.NewGuid();

        _serviceMock.ExecuteAsync(
                Arg.Is<RetrieveDependenciesForDeleteRequest>(r => r!.ObjectId == faultingId),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<OrganizationResponse>(
                new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "dependency fault")));
        SetupDependencies(okId, DependencyRecord(60, formId, "Form"));
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("systemform", formId) { ["name"] = "Account Main Form" }
            ])));

        var results = await WebResourceDependencyChecker.CheckAsync(_serviceMock, [faultingId, okId]);

        var faultingResult = results.Single(r => r!.WebResourceId == faultingId);
        faultingResult.Checked.Should().BeFalse();
        faultingResult.Dependents.Should().BeNull();

        var okResult = results.Single(r => r!.WebResourceId == okId);
        okResult.Checked.Should().BeTrue();
        Assert.Single(okResult.Dependents!);
    }

    [Fact]
    public async Task CheckAsync_OneResourceThrowsNonFaultException_OtherStillReturnsDependents()
    {
        // A transport error / timeout / bad cast doesn't come back as FaultException<OrganizationServiceFault>
        // — it must still degrade that one resource to unchecked rather than aborting the batch (step 4).
        var faultingId = Guid.NewGuid();
        var okId = Guid.NewGuid();
        var formId = Guid.NewGuid();

        _serviceMock.ExecuteAsync(
                Arg.Is<RetrieveDependenciesForDeleteRequest>(r => r!.ObjectId == faultingId),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<OrganizationResponse>(new InvalidOperationException("transport error")));
        SetupDependencies(okId, DependencyRecord(60, formId, "Form"));
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("systemform", formId) { ["name"] = "Account Main Form" }
            ])));

        var results = await WebResourceDependencyChecker.CheckAsync(_serviceMock, [faultingId, okId]);

        var faultingResult = results.Single(r => r!.WebResourceId == faultingId);
        faultingResult.Checked.Should().BeFalse();
        faultingResult.Dependents.Should().BeNull();

        var okResult = results.Single(r => r!.WebResourceId == okId);
        okResult.Checked.Should().BeTrue();
        Assert.Single(okResult.Dependents!);
    }

    [Fact]
    public async Task CheckAsync_RequestCarriesComponentType61AndResourceId()
    {
        var webResourceId = Guid.NewGuid();
        SetupDependencies(webResourceId);

        await WebResourceDependencyChecker.CheckAsync(_serviceMock, [webResourceId]);

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<RetrieveDependenciesForDeleteRequest>(r => r!.ComponentType == 61 && r.ObjectId == webResourceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_MoreThanEightResources_NeverExceedsEightInFlight()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var inFlight = 0;
        var maxObserved = 0;
        var gate = new object();

        async Task<OrganizationResponse> SimulateCallAsync()
        {
            lock (gate)
            {
                inFlight++;
                maxObserved = Math.Max(maxObserved, inFlight);
            }
            await Task.Delay(20);
            lock (gate) { inFlight--; }
            return new RetrieveDependenciesForDeleteResponse
            {
                Results = { ["EntityCollection"] = new EntityCollection() }
            };
        }

        _serviceMock.ExecuteAsync(Arg.Any<RetrieveDependenciesForDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => SimulateCallAsync());

        await WebResourceDependencyChecker.CheckAsync(_serviceMock, ids);

        maxObserved.Should().BeLessThanOrEqualTo(8);
        // >1, not just >0 — proves calls actually overlapped (parallel), not merely that one ran.
        maxObserved.Should().BeGreaterThan(1);
    }
}
