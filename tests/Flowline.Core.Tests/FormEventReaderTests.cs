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
        // R15/R14: a third file with no annotation at all still tracks as a library and doesn't affect forms.
        File.WriteAllText(Path.Combine(_webresourceRoot, "untracked.js"), "console.log('no annotation here');");

        var accountFormId = Guid.NewGuid();
        var contactFormId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupEntityObjectTypeCode("contact", 2);
        _serviceMock.SetupSystemFormsInSolution(
            (accountFormId, "Account Main", "<form>account</form>", "account"),
            (contactFormId, "Contact Main", "<form>contact</form>", "contact"));

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

        // R15: TrackedLibraryNames covers every JS file, not just annotated ones.
        Assert.Equal(3, snapshot.TrackedLibraryNames.Count);
        Assert.Contains("my_MySolution/untracked.js", snapshot.TrackedLibraryNames);

        // R9: only Main (2) and Quick Create (7) form types are queried (solution-scoped systemform query).
        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "systemform" &&
                q.LinkEntities.Count > 0 &&
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
        _serviceMock.SetupSystemFormsInSolution(
            (Guid.NewGuid(), "Account Main", "<form>main</form>", "account"),
            (Guid.NewGuid(), "Account Quick Create", "<form>qc</form>", "account"));

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
        _serviceMock.SetupSystemFormsInSolution((Guid.NewGuid(), "Account Main", "<form>main</form>", "account"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("bad.js", ex.Message);
        Assert.Contains("badentity", ex.Message);

        // The good annotation isn't part of the error list — it still resolved despite the bad one failing.
        Assert.DoesNotContain("good.js", ex.Message);
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
        _serviceMock.SetupSystemFormsInSolution((Guid.NewGuid(), "Account Main", "<form>main</form>", "account"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("bad.js", ex.Message);
        Assert.Contains("badentity", ex.Message);

        // The good annotation isn't part of the error list — it still resolved despite the bad one faulting.
        Assert.DoesNotContain("good.js", ex.Message);
    }

    [Fact]
    public async Task LoadSnapshotAsync_FormNotFound_ShouldThrowNamingFileAndForm()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Missing Form\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        // No solution-scoped or global systemform setup — both default to an empty EntityCollection, so
        // the fallback global lookup finds nothing either: R8's "doesn't exist at all".

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("form.js", ex.Message);
        Assert.Contains("Missing Form", ex.Message);
        Assert.Contains("not found for entity", ex.Message);
    }

    [Fact]
    public async Task LoadSnapshotAsync_FormExistsGloballyButNotInSolution_ShouldThrowDistinctR8aMessage()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        // The form exists in Dataverse generally (global/unscoped query)...
        _serviceMock.SetupSystemForms(1, "Account Main", (Guid.NewGuid(), "Account Main", "<form>main</form>"));
        // ...but the solution-scoped query (left at its default empty EntityCollection) has no such component.

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("form.js", ex.Message);
        Assert.Contains("Account Main", ex.Message);
        Assert.Contains("not a component of solution", ex.Message);
        // Distinct from R8's "doesn't exist at all" message.
        Assert.DoesNotContain("not found for entity", ex.Message);
    }

    [Fact]
    public async Task LoadSnapshotAsync_AmbiguousForm_ShouldThrowNamingAmbiguity()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution(
            (Guid.NewGuid(), "Account Main", "<form>1</form>", "account"),
            (Guid.NewGuid(), "Account Main", "<form>2</form>", "account"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("form.js", ex.Message);
        Assert.Contains("ambiguous", ex.Message);
    }

    [Fact]
    public async Task LoadSnapshotAsync_VerbatimModeFile_ShouldResolveLibraryNameToVerbatimPath()
    {
        var verbatimDir = Path.Combine(_webresourceRoot, "my_ns");
        Directory.CreateDirectory(verbatimDir);
        File.WriteAllText(Path.Combine(verbatimDir, "form.js"),
            "// flowline:onload account \"Account Main\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((Guid.NewGuid(), "Account Main", "<form>account</form>", "account"));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Contains(snapshot.Annotations, a => a.LibraryName == "my_ns/form.js");
        Assert.Contains("my_ns/form.js", snapshot.TrackedLibraryNames);
    }

    [Fact]
    public async Task LoadSnapshotAsync_NoAnnotations_ShouldStillResolveSolutionScopedFormsAndTrackedLibraries()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "plain.js"), "console.log('no annotations');");

        var orphanFormId = Guid.NewGuid();
        // No annotation ever references "account" — its logical name comes straight off the solution-scoped
        // systemform row's objecttypecode (which Dataverse returns as the entity logical name), not via any
        // annotation-driven resolution path.
        _serviceMock.SetupSystemFormsInSolution((orphanFormId, "Account Main", "<form>orphan</form>", "account"));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Empty(snapshot.Annotations);
        Assert.Contains("my_MySolution/plain.js", snapshot.TrackedLibraryNames);

        var orphanForm = Assert.Single(snapshot.Forms).Value;
        Assert.Equal(orphanFormId, orphanForm.Id);
        Assert.Equal("account", orphanForm.EntityLogicalName);
        Assert.Equal("<form>orphan</form>", orphanForm.FormXml);

        // WebResourceReader.LoadSnapshotAsync still ran (it resolves the solution regardless of annotations).
        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadSnapshotAsync_MalformedAnnotationMissingForm_WarnsAndRegistersNothingForThatLine()
    {
        // Regression: a live push registered nothing for a malformed annotation line, with no error or
        // warning at all — the line vanished silently. Must now warn, naming the file, so a user isn't left
        // wondering why nothing happened.
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account\nfunction onLoad() {}");

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Empty(snapshot.Annotations);
        Assert.Contains("form.js", _console.Output);
        Assert.Contains("malformed", _console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadSnapshotAsync_SingleQuotedFormName_RecognizedNotFlaggedMalformed()
    {
        // R3: single quotes are an accepted alternative to double quotes for a multi-word form name.
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account 'Account Quick Create'\nfunction onLoad() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((Guid.NewGuid(), "Account Quick Create", "<form>main</form>", "account"));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Contains(snapshot.Annotations, a => a.Annotation.Form == "Account Quick Create");
        Assert.DoesNotContain("malformed", _console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadSnapshotAsync_SuppressWarnings_NoMalformedWarningPrinted()
    {
        // Cleanup and registration both re-scan the same local JS files (KTD12) — suppressWarnings lets
        // the cleanup pass compute silently so the registration pass is the only one that warns.
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account\nfunction onLoad() {}");

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution", suppressWarnings: true);

        Assert.Empty(snapshot.Annotations);
        Assert.DoesNotContain("malformed", _console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
