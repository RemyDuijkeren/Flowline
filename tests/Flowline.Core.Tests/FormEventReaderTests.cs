using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Services;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class FormEventReaderTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly FormEventReader _reader;
    readonly string _webresourceRoot;

    public FormEventReaderTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _reader = new FormEventReader(_console);
        _webresourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webresourceRoot);

        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OrganizationResponse()));

        _serviceMock.SetupSolution("MySolution", "my");
    }

    public void Dispose()
    {
        if (Directory.Exists(_webresourceRoot))
            Directory.Delete(_webresourceRoot, true);
    }

    [Fact]
    public async Task LoadSnapshotAsync_TwoAnnotationsDifferentForms_ShouldResolveBoth()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form1.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form2.js"),
            "// flowline:onload contact \"Contact Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var accountFormId = Guid.NewGuid();
        var contactFormId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupEntityObjectTypeCode("contact", 2);
        _serviceMock.SetupSystemForms(1, "Account Main", (accountFormId, "Account Main", "<form>account</form>"));
        _serviceMock.SetupSystemForms(2, "Contact Main", (contactFormId, "Contact Main", "<form>contact</form>"));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Equal(2, snapshot.Annotations.Count);
        Assert.Contains(snapshot.Annotations, a => a.LibraryName == "my_MySolution/form1.js" && a.Annotation.Entity == "account");
        Assert.Contains(snapshot.Annotations, a => a.LibraryName == "my_MySolution/form2.js" && a.Annotation.Entity == "contact");

        Assert.Equal(2, snapshot.Forms.Count);
        var accountForm = snapshot.Forms[("account", "Account Main")];
        Assert.Equal(accountFormId, accountForm.Id);
        Assert.Equal("<form>account</form>", accountForm.FormXml);
        var contactForm = snapshot.Forms[("contact", "Contact Main")];
        Assert.Equal(contactFormId, contactForm.Id);
        Assert.Equal("<form>contact</form>", contactForm.FormXml);

        // R9: only Main (2) and Quick Create (7) form types are queried.
        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "systemform" &&
                q.Criteria.Conditions.Any(c => c.AttributeName == "name" && c.Values.Contains("Account Main")) &&
                q.Criteria.Conditions.Any(c => c.AttributeName == "type" &&
                    c.Operator == ConditionOperator.In &&
                    c.Values.Contains(2) && c.Values.Contains(7))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadSnapshotAsync_SameEntityTwoAnnotations_ShouldResolveEntityOnlyOnce()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form1.js"),
            "// flowline:onload account \"Account Main\" handlerA\nfunction handlerA() {}");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form2.js"),
            "// flowline:onsave account \"Account Quick Create\" handlerB\nfunction handlerB() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemForms(1, "Account Main", (Guid.NewGuid(), "Account Main", "<form>main</form>"));
        _serviceMock.SetupSystemForms(1, "Account Quick Create", (Guid.NewGuid(), "Account Quick Create", "<form>qc</form>"));

        await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == "account"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadSnapshotAsync_UnresolvableEntity_ShouldThrowButNotBlockOtherAnnotations()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "bad.js"),
            "// flowline:onload badentity \"Some Form\" handler\nfunction handler() {}");
        File.WriteAllText(Path.Combine(_webresourceRoot, "good.js"),
            "// flowline:onload account \"Account Main\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("badentity", null);
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemForms(1, "Account Main", (Guid.NewGuid(), "Account Main", "<form>main</form>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("bad.js", ex.Message);
        Assert.Contains("badentity", ex.Message);

        // The good annotation still resolved its form despite the bad one failing.
        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "systemform" &&
                q.Criteria.Conditions.Any(c => c.AttributeName == "name" && c.Values.Contains("Account Main"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadSnapshotAsync_EntityResolutionFaults_ShouldThrowButNotBlockOtherAnnotations()
    {
        // A genuinely nonexistent entity logical name faults rather than returning null metadata —
        // this exercises FormEventReader.ResolveObjectTypeCodeAsync's FaultException catch, distinct from
        // the null-metadata case covered by LoadSnapshotAsync_UnresolvableEntity_ShouldThrowButNotBlockOtherAnnotations.
        File.WriteAllText(Path.Combine(_webresourceRoot, "bad.js"),
            "// flowline:onload badentity \"Some Form\" handler\nfunction handler() {}");
        File.WriteAllText(Path.Combine(_webresourceRoot, "good.js"),
            "// flowline:onload account \"Account Main\" handler\nfunction handler() {}");

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == "badentity"),
                Arg.Any<CancellationToken>())
            .Returns<OrganizationResponse>(_ => throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault()));
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemForms(1, "Account Main", (Guid.NewGuid(), "Account Main", "<form>main</form>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("bad.js", ex.Message);
        Assert.Contains("badentity", ex.Message);

        // The good annotation still resolved its form despite the bad one faulting.
        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "systemform" &&
                q.Criteria.Conditions.Any(c => c.AttributeName == "name" && c.Values.Contains("Account Main"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadSnapshotAsync_FormNotFound_ShouldThrowNamingFileAndForm()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Missing Form\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        // No SetupSystemForms override — default RetrieveMultipleAsync returns an empty collection.

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("form.js", ex.Message);
        Assert.Contains("Missing Form", ex.Message);
    }

    [Fact]
    public async Task LoadSnapshotAsync_AmbiguousForm_ShouldThrowNamingAmbiguity()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemForms(1, "Account Main",
            (Guid.NewGuid(), "Account Main", "<form>1</form>"),
            (Guid.NewGuid(), "Account Main", "<form>2</form>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("form.js", ex.Message);
        Assert.Contains("ambiguous", ex.Message);
    }

    [Fact]
    public async Task LoadSnapshotAsync_NoAnnotations_ShouldReturnEmptySnapshotWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "plain.js"), "console.log('no annotations');");

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Empty(snapshot.Annotations);
        Assert.Empty(snapshot.Forms);

        // WebResourceReader.LoadSnapshotAsync still ran (it resolves the solution regardless of annotations).
        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>());
    }
}
