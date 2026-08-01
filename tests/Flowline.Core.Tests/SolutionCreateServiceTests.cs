using System.ServiceModel;
using Flowline.Core.Services;
using FluentAssertions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;

namespace Flowline.Core.Tests;

public class SolutionCreateServiceTests
{
    readonly IOrganizationServiceAsync2 _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
    readonly SolutionCreateService _service = new();

    public SolutionCreateServiceTests()
    {
        // Default: no existing publisher, no existing solution -- individual tests override.
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    [Fact]
    public async Task CreateAsync_ExistingPublisherPrefix_ReusesPublisher_DoesNotCreateNewOne()
    {
        var existingPublisherId = Guid.NewGuid();
        var existingPublisher = new Entity("publisher", existingPublisherId);
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "publisher")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([existingPublisher])));

        var solutionId = Guid.NewGuid();
        _serviceMock.CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(solutionId));

        var result = await _service.CreateAsync(_serviceMock, "TestSolution", "Test Solution", "acme");

        result.PublisherId.Should().Be(existingPublisherId);
        result.PublisherCreated.Should().BeFalse();
        result.SolutionId.Should().Be(solutionId);

        await _serviceMock.DidNotReceive().CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "publisher")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_NewPublisherPrefix_CreatesPublisherWithDerivedOptionValuePrefix()
    {
        var newPublisherId = Guid.NewGuid();
        Entity? capturedPublisher = null;
        _serviceMock.CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "publisher")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(newPublisherId))
            .AndDoes(callInfo => capturedPublisher = callInfo.Arg<Entity>());
        _serviceMock.CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Guid.NewGuid()));

        var result = await _service.CreateAsync(_serviceMock, "TestSolution", "Test Solution", "acme");

        result.PublisherId.Should().Be(newPublisherId);
        result.PublisherCreated.Should().BeTrue();
        result.PublisherPrefix.Should().Be("acme");

        capturedPublisher.Should().NotBeNull();
        capturedPublisher!.GetAttributeValue<string>("uniquename").Should().Be("acme");
        capturedPublisher.GetAttributeValue<string>("friendlyname").Should().Be("acme");
        capturedPublisher.GetAttributeValue<string>("customizationprefix").Should().Be("acme");

        var optionValuePrefix = capturedPublisher.GetAttributeValue<int>("customizationoptionvalueprefix");
        optionValuePrefix.Should().BeInRange(10000, 99999);
    }

    [Fact]
    public void DeriveOptionValuePrefix_SamePrefix_IsDeterministicAcrossCalls()
    {
        var first = SolutionCreateService.DeriveOptionValuePrefix("acme");
        var second = SolutionCreateService.DeriveOptionValuePrefix("acme");

        first.Should().Be(second);
        first.Should().BeInRange(10000, 99999);
    }

    [Fact]
    public void DeriveOptionValuePrefix_DifferentPrefixes_ProduceDifferentValues()
    {
        var acme = SolutionCreateService.DeriveOptionValuePrefix("acme");
        var contoso = SolutionCreateService.DeriveOptionValuePrefix("contoso");

        acme.Should().NotBe(contoso);
    }

    [Fact]
    public async Task CreateAsync_ExistingSolutionUniqueName_ThrowsConflict_DoesNotCreateSolution()
    {
        var existingPublisher = new Entity("publisher", Guid.NewGuid());
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "publisher")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([existingPublisher])));

        var existingSolution = new Entity("solution", Guid.NewGuid());
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([existingSolution])));

        var act = () => _service.CreateAsync(_serviceMock, "TestSolution", "Test Solution", "acme");

        var thrown = (await act.Should().ThrowAsync<FlowlineException>())
            .Which;
        thrown.ExitCode.Should().Be(ExitCode.ValidationFailed);
        thrown.Message.Should().Contain("TestSolution").And.Contain("already exists");

        await _serviceMock.DidNotReceive().CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "solution")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListPublishersAsync_ReturnsPrefixAndFriendlyName_OrderedByPrefix()
    {
        var zulu = new Entity("publisher") { ["customizationprefix"] = "zulu", ["friendlyname"] = "Zulu Corp" };
        var acme = new Entity("publisher") { ["customizationprefix"] = "acme", ["friendlyname"] = "Acme Corp" };
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "publisher")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([zulu, acme])));

        var result = await _service.ListPublishersAsync(_serviceMock);

        result.Should().HaveCount(2);
        result[0].Prefix.Should().Be("acme");
        result[0].FriendlyName.Should().Be("Acme Corp");
        result[1].Prefix.Should().Be("zulu");
    }

    [Fact]
    public async Task CreateAsync_PrivilegeFaultOnPublisherCreate_ThrowsFlowlineExceptionNamingPermission()
    {
        _serviceMock.CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "publisher")), Arg.Any<CancellationToken>())
            .Returns<Guid>(_ => throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault(), "Principal user is missing prvCreatePublisher privilege."));

        var act = () => _service.CreateAsync(_serviceMock, "TestSolution", "Test Solution", "acme");

        var thrown = (await act.Should().ThrowAsync<FlowlineException>())
            .Which;
        thrown.ExitCode.Should().Be(ExitCode.ValidationFailed);
        thrown.Message.Should().Contain("Publisher").And.Contain("permission");
        thrown.InnerException.Should().BeOfType<FaultException<OrganizationServiceFault>>();
    }
}
