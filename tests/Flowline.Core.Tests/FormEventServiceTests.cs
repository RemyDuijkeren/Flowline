using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console.Testing;
using static Flowline.Core.Tests.FormEventTestHelpers;

namespace Flowline.Core.Tests;

// U7: FormEventService orchestrates Reader → Planner → Executor behind two entry points
// (CleanupOrphanedAsync/RegisterAsync — KTD12's two-phase split). Mocking approach mirrors
// FormEventReaderTests (solution/entity-metadata/systemform setup) and WebResourceServiceTests
// (SetupSolution/SetupWebResources shape) since the reader constructs its own WebResourceReader
// internally (KTD10) and needs the same solution/webresource plumbing to resolve local names.
public class FormEventServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly FormEventService _service;
    readonly string _webresourceRoot;

    public FormEventServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _service = new FormEventService(_console);
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
    public async Task CleanupOrphanedAsync_NoTrackedLibrariesOrStaleHandlers_ShouldNoOpAndReturnFalse()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "plain.js"), "console.log('no annotations');");

        var result = await _service.CleanupOrphanedAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);

        Assert.False(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_NoAnnotations_ShouldNoOpAndReturnFalse()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "plain.js"), "console.log('no annotations');");

        var result = await _service.RegisterAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);

        Assert.False(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
        // Complements CleanupOrphanedAsync_EmptyPlan_ShouldReturnFalseSilentlyWithoutSkipMessage — the
        // registration phase is the one that's still allowed to report "up to date" to the user.
        Assert.Contains("already up to date", _console.Output);
    }

    [Fact]
    public async Task RegisterAsync_NewAnnotationOnExistingForm_ShouldUpdateAndPublishReturnTrue()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form></form>", "account"));

        var result = await _service.RegisterAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);

        Assert.True(result);
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.Id == formId), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_DryRunWithPendingChanges_ShouldNotWriteButShowRichPreviewReturnTrue()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form></form>", "account"));

        var result = await _service.RegisterAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: true);

        Assert.True(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());

        // R18b: the removed short-circuit means the executor's own rich per-handler preview is now
        // reachable — not just a bare "N form(s) pending" count.
        Assert.Contains("1 handler(s) added", _console.Output);
        Assert.Contains("Handlers added (1)", _console.Output);
        Assert.Contains("Run without --dry-run to apply", _console.Output);
    }

    [Fact]
    public async Task CleanupOrphanedAsync_DryRunWithStaleHandler_ShouldReturnTrueWithoutPrintingDuplicatePreview()
    {
        // ExecuteAsync's dry-run branch never consults cleanupOnly — it prints the preview and returns
        // before that point — so calling it from the cleanup phase too would duplicate the exact same
        // block the registration phase already prints for the same plan. FormEventService instead skips
        // the executor call entirely for this phase and just reports "has pending changes" (R18b, deduped).
        File.WriteAllText(Path.Combine(_webresourceRoot, "stale.js"), "// no annotation here");

        var formId = Guid.NewGuid();
        var staleLibrary = new FormLibraryEntry("my_MySolution/stale.js", FormEventDeterministicId.ForLibrary("my_MySolution/stale.js"));
        var staleHandler = new FormHandler("legacyFn", "my_MySolution/stale.js",
            FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "legacyFn", "my_MySolution/stale.js"), "");
        var currentFormXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormHandler> { staleHandler }, new HashSet<FormLibraryEntry> { staleLibrary });

        // No annotation names "account" in this scenario (R14: the form is only discovered via the
        // solution-scoped join) — its logical name comes straight off the solution-scoped systemform row's
        // objecttypecode (which Dataverse returns as the entity logical name).
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", currentFormXml, "account"));

        var result = await _service.CleanupOrphanedAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: true);

        Assert.True(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("Run without --dry-run to apply", _console.Output);
        Assert.DoesNotContain("Summary:", _console.Output);
    }

    [Fact]
    public async Task CleanupOrphanedAsync_EmptyPlan_ShouldReturnFalseSilentlyWithoutSkipMessage()
    {
        // R18b's dedup fix (above) applies to the "up to date" skip message too — the cleanup phase must
        // stay silent so a fully clean push doesn't print the same line twice (once per phase).
        File.WriteAllText(Path.Combine(_webresourceRoot, "plain.js"), "console.log('no annotations');");

        var result = await _service.CleanupOrphanedAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);

        Assert.False(result);
        Assert.DoesNotContain("already up to date", _console.Output);
    }

    [Fact]
    public async Task CleanupThenRegister_DryRun_PreviewPrintedExactlyOnceNotTwice()
    {
        // Direct regression test for the double-print bug: Reader+Planner compute the identical plan for
        // both phases (cleanupOnly only affects the executor's write, which dry-run never reaches), so
        // without the dedup in FormEventService, calling both phases in dry-run would render the same
        // "Summary: ... Handlers added (...)" block twice.
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form></form>", "account"));

        var cleanupResult = await _service.CleanupOrphanedAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: true);
        var registerResult = await _service.RegisterAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: true);

        Assert.True(cleanupResult);
        Assert.True(registerResult);

        var occurrences = _console.Output.Split("Handlers added").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task CleanupThenRegister_MalformedAnnotation_WarningPrintedExactlyOnceNotTwice()
    {
        // Direct regression test: reported live — a malformed-annotation warning (reader-level, unrelated
        // to any pending handler/library change) printed once per phase since FormEventReader.LoadSnapshotAsync
        // re-scans the same local JS files on both the cleanup and registration pass, with no dedup gate of
        // its own (unlike the plan-level dry-run preview / "up to date" skip message).
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), "// flowline:onload account\ncode();");

        var cleanupResult = await _service.CleanupOrphanedAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);
        var registerResult = await _service.RegisterAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);

        Assert.False(cleanupResult);
        Assert.False(registerResult);

        var occurrences = _console.Output.Split("malformed flowline annotation").Length - 1;
        Assert.Equal(1, occurrences);
    }

    // KTD12's load-bearing regression test: proves the cleanup pass' write completes strictly before the
    // registration pass' write is issued, for the exact scenario the bug is about — a stale handler safe to
    // remove now, and a brand-new handler whose library isn't registered on the form until the registration
    // pass runs. No true PushCommand-level integration harness exists (PushCommandTests.cs only covers
    // static helpers — ExecuteFlowlineAsync depends on the static FlowlineValidator.Default and can't be
    // exercised with mocks without deeper surgery), so this asserts ordering directly against the shared
    // IOrganizationServiceAsync2 mock's call sequence, mirroring exactly how PushCommand.cs sequences the
    // two calls (await CleanupOrphanedAsync(...) then await RegisterAsync(...)).
    [Fact]
    public async Task CleanupThenRegister_StaleHandlerRemovalCompletesBeforeNewHandlerRegistrationWrite()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "stale.js"), "// no annotation here");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        var staleLibrary = new FormLibraryEntry("my_MySolution/stale.js", FormEventDeterministicId.ForLibrary("my_MySolution/stale.js"));
        var staleHandler = new FormHandler("legacyFn", "my_MySolution/stale.js",
            FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "legacyFn", "my_MySolution/stale.js"), "");
        var currentFormXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormHandler> { staleHandler }, new HashSet<FormLibraryEntry> { staleLibrary });

        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", currentFormXml, "account"));

        var writtenFormXmlInCallOrder = new List<string>();
        _serviceMock.UpdateAsync(Arg.Do<Entity>(e => writtenFormXmlInCallOrder.Add((string)e["formxml"])), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Mirrors PushCommand.cs's exact sequencing: cleanup awaited to completion before registration starts.
        var cleanupResult = await _service.CleanupOrphanedAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);
        var registerResult = await _service.RegisterAsync(_serviceMock, _webresourceRoot, "MySolution", force: false, dryRun: false);

        Assert.True(cleanupResult);
        Assert.True(registerResult);
        Assert.Equal(2, writtenFormXmlInCallOrder.Count);

        // Call 0 (cleanup, cleanupOnly:true): the stale handler's library isn't referenced by any new
        // handler yet on the form, so its removal is safe and written — but the brand-new handler is
        // deferred (its library isn't on the form yet), proving cleanupOnly narrowed the write.
        Assert.DoesNotContain("legacyFn", writtenFormXmlInCallOrder[0]);
        Assert.DoesNotContain("onLoadHandler", writtenFormXmlInCallOrder[0]);

        // Call 1 (registration, cleanupOnly:false): now that call 0 already landed, the registration pass
        // both drops the stale handler (still absent from Desired) and adds the new one unfiltered.
        Assert.DoesNotContain("legacyFn", writtenFormXmlInCallOrder[1]);
        Assert.Contains("onLoadHandler", writtenFormXmlInCallOrder[1]);
    }
}
