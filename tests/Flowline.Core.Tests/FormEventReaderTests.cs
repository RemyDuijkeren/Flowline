using System.ServiceModel;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core.FormEvents;
using Flowline.Core.FormEvents.Support;
using Flowline.Core.WebResources;
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
        _console.Profile.Width = 400; // avoid word-wrap splitting assertion substrings across lines
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
    public async Task LoadSnapshotAsync_ExtensionlessLocalFile_ShouldNotPrintWebResourceReaderWarning()
    {
        // Extensionless files go through WebResourceReader's own Tier 1/2 type-fallback, which warns —
        // but that warning belongs solely to WebResourceService's own pass (KTD12); this reader re-scans
        // the same files for annotations and must never surface it, in either the cleanup or registration
        // pass, or a single push prints it 2-3 times over.
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_legacyscript"), "function legacy() {}");

        await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.DoesNotContain("has no extension", _console.Output);
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

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
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

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
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

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
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

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
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

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
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
    public async Task LoadSnapshotAsync_SolutionScopedForm_CarriesRowVersionIntoSnapshotForOptimisticConcurrency()
    {
        // RowVersion is a first-class SDK property (not a regular attribute), populated on every real
        // Dataverse retrieve regardless of ColumnSet — confirmed live. FormEventExecutor's optimistic
        // concurrency check depends on this actually reaching DataverseForm.RowVersion.
        var formId = Guid.NewGuid();
        var entities = new List<Entity>
        {
            new("systemform", formId)
            {
                ["name"] = "Account Main", ["formxml"] = "<form/>", ["objecttypecode"] = "account",
                RowVersion = "12345"
            }
        };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "systemform" && q.LinkEntities.Count > 0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        var form = Assert.Single(snapshot.Forms).Value;
        Assert.Equal("12345", form.RowVersion);
    }

    [Fact]
    public async Task LoadSnapshotAsync_SolutionScopedFormMissingObjectTypeCode_WarnsAndSkipsForm()
    {
        // A systemform row can theoretically come back with no objecttypecode (defensive case — not
        // reproduced live, but the reader guards it explicitly). Must warn (naming the form) and exclude
        // it from snapshot.Forms entirely, rather than crash or silently include a form with no entity.
        var brokenFormId = Guid.NewGuid();
        var entities = new List<Entity>
        {
            new("systemform", brokenFormId) { ["name"] = "Broken Form", ["formxml"] = "<form/>", ["objecttypecode"] = null! }
        };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "systemform" && q.LinkEntities.Count > 0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Empty(snapshot.Forms);
        Assert.Contains("Broken Form", _console.Output);
        Assert.Contains("no objecttypecode", _console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadSnapshotAsync_SolutionScopedFormMissingObjectTypeCode_SuppressWarningsSilencesIt()
    {
        var brokenFormId = Guid.NewGuid();
        var entities = new List<Entity>
        {
            new("systemform", brokenFormId) { ["name"] = "Broken Form", ["formxml"] = "<form/>", ["objecttypecode"] = null! }
        };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "systemform" && q.LinkEntities.Count > 0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution", suppressWarnings: true);

        Assert.Empty(snapshot.Forms);
        Assert.DoesNotContain("no objecttypecode", _console.Output, StringComparison.OrdinalIgnoreCase);
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

    // U2: FormEventReader wires FormEventIdentityCache into the solution-scoped happy path so a later
    // rename-detection unit (U3) has data on every successful resolution to suggest from.
    [Fact]
    public async Task LoadSnapshotAsync_FormResolves_WritesCacheEntry()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form>account</form>", "account"));

        var cachePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution", cachePath);

            var cache = new FormEventIdentityCache(cachePath);
            Assert.Equal(formId, cache.TryGet("account", "Account Main"));
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task LoadSnapshotAsync_SameFormResolvedByTwoAnnotations_WritesOneCacheEntry()
    {
        // onLoad and onSave annotations on different files sharing the same form — resolvedForms is keyed
        // by (Entity, Form), so this must write exactly one cache entry, not a conflicting pair.
        File.WriteAllText(Path.Combine(_webresourceRoot, "form1.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form2.js"),
            "// flowline:onsave account \"Account Main\" onSaveHandler\nfunction onSaveHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form>account</form>", "account"));

        var cachePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution", cachePath);

            var cache = new FormEventIdentityCache(cachePath);
            Assert.Equal(formId, cache.TryGet("account", "Account Main"));

            var entries = JsonSerializer.Deserialize<FormEventIdentityCache.Entry[]>(File.ReadAllText(cachePath));
            Assert.Single(entries!);
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    // U3/R6: no advisory signal identifies a candidate here (no systemform at all is set up for "account")
    // — the thrown message must be byte-for-byte identical to today's original literal, proving the
    // advisor's "nothing found" path never mutates the existing failure text.
    [Fact]
    public async Task LoadSnapshotAsync_FormNotFound_NoAdvisorySignal_ErrorMessageByteForByteUnchanged()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Missing Form\" handler\nfunction handler() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        // No solution-scoped or global systemform setup at all — zero candidates for "account", so none
        // of the self-tag/cache/sole-survivor signals can fire.

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Equal(
            "Form event annotations failed to resolve:\n"
            + "form.js: form 'Missing Form' not found for entity 'account' (Main or Quick Create form).",
            ex.Message);
    }

    // U3: integration through the real LoadSnapshotAsync pipeline — a live form still carries the
    // deterministic handler id that "Old Main" would have produced, even though it's now named "New Main".
    // Asserts the exact enriched FlowlineException.Message text for a self-tag-match rename.
    [Fact]
    public async Task LoadSnapshotAsync_FormRenamedSelfTagMatch_EnrichesNotFoundMessageWithSuggestion()
    {
        const string library = "my_MySolution/form.js";
        var expectedId = FormEventDeterministicId.ForHandler("account", "Old Main", FormEventType.OnLoad, "onLoad", library);
        var formXmlWithHandler = FormEventTestHelpers.BuildFormXml(FormEventType.OnLoad,
            new HashSet<FormEventHandler> { new("onLoad", library, expectedId, "") });

        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Old Main\"\nfunction onLoad() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((Guid.NewGuid(), "New Main", formXmlWithHandler, "account"));
        // Global fallback lookup for "Old Main" also finds nothing (not configured) — R8's "doesn't exist
        // at all", the same branch the enrichment attaches to.

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Equal(
            "Form event annotations failed to resolve:\n"
            + "form.js: form 'Old Main' not found for entity 'account' (Main or Quick Create form)."
            + $"{Environment.NewLine}      — this form was renamed to 'New Main' — same handler signature"
            + $"{Environment.NewLine}      — update your annotation to:"
            + $"{Environment.NewLine}        // flowline:onload account \"New Main\"",
            ex.Message);
    }

    // R5 — the single most safety-critical requirement: a name-lookup miss ALWAYS fails the push, even
    // when the rename advisor found a strong (self-tag) candidate. No signal, alone or combined, may let
    // registration proceed or succeed silently.
    [Fact]
    public async Task LoadSnapshotAsync_RenameCandidateFound_StillThrowsFlowlineException_R5Regression()
    {
        const string library = "my_MySolution/form.js";
        var expectedId = FormEventDeterministicId.ForHandler("account", "Old Main", FormEventType.OnLoad, "onLoad", library);
        var formXmlWithHandler = FormEventTestHelpers.BuildFormXml(FormEventType.OnLoad,
            new HashSet<FormEventHandler> { new("onLoad", library, expectedId, "") });

        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Old Main\"\nfunction onLoad() {}");

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((Guid.NewGuid(), "New Main", formXmlWithHandler, "account"));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
        // A confident candidate was found and named in the message...
        Assert.Contains("New Main", ex.Message);
        // ...but nothing resolved: the annotation for "Old Main" never made it into a valid snapshot —
        // the exception above is what proves the push failed regardless of the suggestion's existence.
    }

    [Fact]
    public async Task LoadSnapshotAsync_CalledTwiceWithSameCachePath_SecondRunOverwritesWithoutError()
    {
        // Mirrors FormEventService.SyncAsync's two-phase design (KTD12: cleanup pass, then registration
        // pass) — both phases re-read the snapshot and reuse the same cache path within one push.
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form>account</form>", "account"));

        var cachePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution", cachePath, suppressWarnings: true);
            await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution", cachePath);

            var cache = new FormEventIdentityCache(cachePath);
            Assert.Equal(formId, cache.TryGet("account", "Account Main"));

            var entries = JsonSerializer.Deserialize<FormEventIdentityCache.Entry[]>(File.ReadAllText(cachePath));
            Assert.Single(entries!);
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }
}
