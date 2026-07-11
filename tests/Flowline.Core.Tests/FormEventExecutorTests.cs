using System.ServiceModel;
using System.Xml.Linq;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using NSubstitute;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console.Testing;
using Xunit;
using static Flowline.Core.Tests.FormEventTestHelpers;

namespace Flowline.Core.Tests;

public class FormEventExecutorTests
{
    readonly IOrganizationServiceAsync2 _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
    readonly TestConsole _console = new();
    readonly FormEventExecutor _executor;

    public FormEventExecutorTests()
    {
        _console.Profile.Width = 400; // avoid word-wrap splitting assertion substrings across lines
        _executor = new FormEventExecutor(_console);
    }

    static FormEventSnapshot BuildSnapshot(params DataverseForm[] forms) =>
        new([], new HashSet<string>(), forms.ToDictionary(f => (f.EntityLogicalName, f.Name), f => f));

    static FormEventSyncPlan BuildPlan(params FormEventFormPlan[] forms)
    {
        var plan = new FormEventSyncPlan();
        plan.Forms.AddRange(forms);
        return plan;
    }

    // Captures every UpdateAsync call's formxml, keyed by systemform id, so assertions can read back the
    // actual mutated XML via FormXmlEventSerializer rather than string-matching raw XML.
    Dictionary<Guid, string> CaptureUpdatedFormXml()
    {
        var captured = new Dictionary<Guid, string>();
        _serviceMock.UpdateAsync(Arg.Do<Entity>(e => captured[e.Id] = (string)e["formxml"]), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return captured;
    }

    static IReadOnlySet<FormHandler> GetHandlersFromCapturedXml(Dictionary<Guid, string> captured, Guid formId, FormEventType evt) =>
        FormXmlEventSerializer.GetHandlers(XDocument.Parse(captured[formId]), evt);

    static IReadOnlySet<FormLibraryEntry> GetLibrariesFromCapturedXml(Dictionary<Guid, string> captured, Guid formId) =>
        FormXmlEventSerializer.GetLibraries(XDocument.Parse(captured[formId]));

    [Fact]
    public async Task ExecuteAsync_NoUnrecognizedHandlers_NoConfirmationPromptUpdatesApplyDirectly()
    {
        var formId = Guid.NewGuid();
        var handler = new FormHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var library = new FormLibraryEntry("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry> { library });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        await _serviceMock.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("unrecognized", _console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(handler, GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad));
        Assert.Contains(library, GetLibrariesFromCapturedXml(captured, formId));
    }

    [Fact]
    public async Task ExecuteAsync_FormWithBothOnLoadAndOnSavePlans_MergedIntoSingleUpdateNeitherEventClobbered()
    {
        // FormEventPlanner groups by (entity, form, event) — a form with both onLoad and onSave
        // annotations produces two FormEventFormPlan entries sharing the same FormId. Each must land in
        // one UpdateAsync call with both events' handlers intact, not two calls where the second
        // overwrites the first (last write wins against the same pristine formxml).
        var formId = Guid.NewGuid();
        var onLoadHandler = new FormHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var onSaveHandler = new FormHandler("onSave", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnSave, "onSave", "av_/lib.js"), "");
        var library = new FormLibraryEntry("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var onLoadPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { onLoadHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry> { library });
        var onSavePlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnSave,
            new HashSet<FormHandler> { onSaveHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry> { library });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(onLoadPlan, onSavePlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        await _serviceMock.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        Assert.Contains(onLoadHandler, GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad));
        Assert.Contains(onSaveHandler, GetHandlersFromCapturedXml(captured, formId, FormEventType.OnSave));
        Assert.Contains(library, GetLibrariesFromCapturedXml(captured, formId));
    }

    [Fact]
    public async Task ExecuteAsync_UnrecognizedHandlersInteractiveConfirms_ProceedsHandlersRemoved()
    {
        var formId = Guid.NewGuid();
        var recognized = new FormHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var unrecognized = new FormHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        const string proposedAnnotation = "// flowline:onload account \"Account Main\" manualFn";
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { recognized, unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, proposedAnnotation) },
            new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        _console.Interactive();
        _console.Input.PushTextWithEnter("y");

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        var written = GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad);
        Assert.Contains(recognized, written);
        Assert.DoesNotContain(unrecognized, written);
        Assert.Contains(proposedAnnotation, _console.Output); // R18a: proposed annotation shown in the interactive prompt too
    }

    [Fact]
    public async Task ExecuteAsync_UnrecognizedHandlersInteractiveDeclines_OnlyThatRemovalExcludedOtherActionsApply()
    {
        var formIdA = Guid.NewGuid();
        var existingRecognized = new FormHandler("onLoad", "av_/keep.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js"), "");
        var newRecognized = new FormHandler("onSave", "av_/keep.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onSave", "av_/keep.js"), "");
        var unrecognized = new FormHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { existingRecognized, newRecognized, unrecognized },
            new HashSet<UnrecognizedHandler> { new(unrecognized, "") },
            new HashSet<FormLibraryEntry>());

        // Second, unrelated form with no unrecognized handlers — must still be updated regardless of the
        // decline decision on formPlanA.
        var formIdB = Guid.NewGuid();
        var contactHandler = new FormHandler("onLoad", "av_/contact.js", FormEventDeterministicId.ForHandler("contact", "Contact Main", FormEventType.OnLoad, "onLoad", "av_/contact.js"), "");
        var formPlanB = new FormEventFormPlan(formIdB, "contact", "Contact Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { contactHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(
            new DataverseForm(formIdA, "Account Main", "account", BuildFormXml()),
            new DataverseForm(formIdB, "Contact Main", "contact", BuildFormXml()));
        var plan = BuildPlan(formPlanA, formPlanB);
        var captured = CaptureUpdatedFormXml();

        _console.Interactive();
        _console.Input.PushTextWithEnter("n");

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        var writtenA = GetHandlersFromCapturedXml(captured, formIdA, FormEventType.OnLoad);
        Assert.Contains(existingRecognized, writtenA);
        Assert.Contains(newRecognized, writtenA); // new plan action still applied on the same form
        Assert.Contains(unrecognized, writtenA); // decline → removal excluded, handler stays

        var writtenB = GetHandlersFromCapturedXml(captured, formIdB, FormEventType.OnLoad);
        Assert.Contains(contactHandler, writtenB); // other form's plan action still applies
    }

    [Fact]
    public async Task ExecuteAsync_UnrecognizedHandlersNonInteractiveNoForce_ThrowsBeforeApplyingAnythingNamesEveryHandler()
    {
        var formId = Guid.NewGuid();
        var unrecognized = new FormHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        const string proposedAnnotation = "// flowline:onload account \"Account Main\" manualFn";
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, proposedAnnotation) },
            new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);

        // _console defaults to non-interactive (TestConsole.Profile.Capabilities.Interactive == false).
        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false));

        Assert.Equal(ExitCode.ForceRequired, ex.ExitCode);
        Assert.Contains("account", ex.Message);
        Assert.Contains("Account Main", ex.Message);
        Assert.Contains("manualFn", ex.Message);
        Assert.Contains("av_/manual.js", ex.Message);
        Assert.Contains(proposedAnnotation, ex.Message); // R18a: proposed annotation shown alongside removal warning

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UnrecognizedHandlersNonInteractiveForce_ProceedsWithoutPrompting()
    {
        var formId = Guid.NewGuid();
        var recognized = new FormHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var unrecognized = new FormHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { recognized, unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, "") },
            new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        // _console stays non-interactive — force must skip the prompt entirely.
        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: true, dryRun: false, cleanupOnly: false);

        Assert.DoesNotContain("unrecognized", _console.Output, StringComparison.OrdinalIgnoreCase);
        var written = GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad);
        Assert.Contains(recognized, written);
        Assert.DoesNotContain(unrecognized, written);
    }

    [Fact]
    public async Task ExecuteAsync_TwoFormsSameEntity_SinglePublishXmlCallForThatEntity()
    {
        var formIdA = Guid.NewGuid();
        var formIdB = Guid.NewGuid();
        var handlerA = new FormHandler("onLoad", "av_/a.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/a.js"), "");
        var handlerB = new FormHandler("onLoad", "av_/b.js", FormEventDeterministicId.ForHandler("account", "Account QuickCreate", FormEventType.OnLoad, "onLoad", "av_/b.js"), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerA }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());
        var formPlanB = new FormEventFormPlan(formIdB, "account", "Account QuickCreate", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerB }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(
            new DataverseForm(formIdA, "Account Main", "account", BuildFormXml()),
            new DataverseForm(formIdB, "Account QuickCreate", "account", BuildFormXml()));
        var plan = BuildPlan(formPlanA, formPlanB);
        CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        await _serviceMock.Received(2).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml" && ((string)r.Parameters["ParameterXml"]).Contains("<entity>account</entity>")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_TwoDifferentEntities_TwoSeparatePublishXmlCalls()
    {
        var formIdA = Guid.NewGuid();
        var formIdB = Guid.NewGuid();
        var handlerA = new FormHandler("onLoad", "av_/a.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/a.js"), "");
        var handlerB = new FormHandler("onLoad", "av_/b.js", FormEventDeterministicId.ForHandler("contact", "Contact Main", FormEventType.OnLoad, "onLoad", "av_/b.js"), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerA }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());
        var formPlanB = new FormEventFormPlan(formIdB, "contact", "Contact Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerB }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(
            new DataverseForm(formIdA, "Account Main", "account", BuildFormXml()),
            new DataverseForm(formIdB, "Contact Main", "contact", BuildFormXml()));
        var plan = BuildPlan(formPlanA, formPlanB);
        CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml" && ((string)r.Parameters["ParameterXml"]).Contains("<entity>account</entity>")),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml" && ((string)r.Parameters["ParameterXml"]).Contains("<entity>contact</entity>")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateAsyncFailureOnOneForm_OtherFormsStillAttemptFailureSurfaced()
    {
        var formIdFails = Guid.NewGuid();
        var formIdOk = Guid.NewGuid();
        var handlerFails = new FormHandler("onLoad", "av_/fails.js", FormEventDeterministicId.ForHandler("account", "Account Fails", FormEventType.OnLoad, "onLoad", "av_/fails.js"), "");
        var handlerOk = new FormHandler("onLoad", "av_/ok.js", FormEventDeterministicId.ForHandler("account", "Account Ok", FormEventType.OnLoad, "onLoad", "av_/ok.js"), "");
        var formPlanFails = new FormEventFormPlan(formIdFails, "account", "Account Fails", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerFails }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());
        var formPlanOk = new FormEventFormPlan(formIdOk, "account", "Account Ok", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerOk }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(
            new DataverseForm(formIdFails, "Account Fails", "account", BuildFormXml()),
            new DataverseForm(formIdOk, "Account Ok", "account", BuildFormXml()));
        var plan = BuildPlan(formPlanFails, formPlanOk);

        _serviceMock.UpdateAsync(Arg.Is<Entity>(e => e.Id == formIdFails), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault())));
        _serviceMock.UpdateAsync(Arg.Is<Entity>(e => e.Id == formIdOk), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false));

        Assert.Contains("1 form event operation(s) failed", ex.Message);
        Assert.Contains("Account Fails", _console.Output);

        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.Id == formIdFails), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.Id == formIdOk), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PublishXmlFailureForOneEntity_SurfacedAsHardFailureOtherEntitiesStillAttempt()
    {
        var formIdA = Guid.NewGuid();
        var formIdB = Guid.NewGuid();
        var handlerA = new FormHandler("onLoad", "av_/a.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/a.js"), "");
        var handlerB = new FormHandler("onLoad", "av_/b.js", FormEventDeterministicId.ForHandler("contact", "Contact Main", FormEventType.OnLoad, "onLoad", "av_/b.js"), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerA }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());
        var formPlanB = new FormEventFormPlan(formIdB, "contact", "Contact Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { handlerB }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(
            new DataverseForm(formIdA, "Account Main", "account", BuildFormXml()),
            new DataverseForm(formIdB, "Contact Main", "contact", BuildFormXml()));
        var plan = BuildPlan(formPlanA, formPlanB);
        CaptureUpdatedFormXml();

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml" && ((string)r.Parameters["ParameterXml"]).Contains("account")),
                Arg.Any<CancellationToken>())
            .Returns<OrganizationResponse>(_ => throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false));

        Assert.Contains("1 form event operation(s) failed", ex.Message);
        Assert.Contains("account", _console.Output);

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml" && ((string)r.Parameters["ParameterXml"]).Contains("contact")),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.Id == formIdA), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.Id == formIdB), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DryRunWithUnrecognizedHandlers_PrintsDetailAndAnnotationsWritesNothingNoPrompt()
    {
        var formId = Guid.NewGuid();
        var recognized = new FormHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var unrecognized = new FormHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        const string proposedAnnotation = "// flowline:onload account \"Account Main\" manualFn";
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { recognized, unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, proposedAnnotation) },
            new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);

        // Interactive with no input queued: if the confirmation prompt fired, TestConsole would throw
        // reading unqueued input — the test failing that way is itself proof the prompt never ran.
        _console.Interactive();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: true, cleanupOnly: false);

        Assert.Contains("manualFn", _console.Output);
        Assert.Contains("av_/manual.js", _console.Output);
        Assert.Contains(proposedAnnotation, _console.Output);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DryRunWithParametersOnlyChange_ReportedAsUpdatedNotSilentlyDropped()
    {
        // FormHandler's identity equality ignores Parameters, so a Parameters-only change is present in
        // both "desired" and "current" by identity — the summary must not let that fall through the
        // added/removed counts as if nothing changed (R18b: the preview must be honest about updates too).
        var formId = Guid.NewGuid();
        var currentHandler = new FormHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "oldParams");
        var desiredHandler = currentHandler with { Parameters = "newParams" };
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { desiredHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibraryEntry>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormHandler> { currentHandler })));
        var plan = BuildPlan(formPlan);

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: true, cleanupOnly: false);

        Assert.Contains("0 handler(s) would be added", _console.Output);
        Assert.Contains("1 updated", _console.Output);
        Assert.Contains("0 removed", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupOnlyMixOfNewAndStaleHandler_OnlyStaleRemovalWrittenNewExcluded()
    {
        var formId = Guid.NewGuid();
        var staleHandler = new FormHandler("legacyFn", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "legacyFn", "av_/lib.js"), "");
        var newHandler = new FormHandler("onLoad", "av_/new.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/new.js"), "");
        var newLibrary = new FormLibraryEntry("av_/new.js", FormEventDeterministicId.ForLibrary("av_/new.js"));
        var currentLibrary = new FormLibraryEntry("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        // Planner already excludes a stale-but-deterministic-id handler from DesiredHandlers entirely (it's
        // simply not re-added on write) — only the new handler is in Desired here. staleHandler exists only
        // on the current form's formxml below.
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { newHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibraryEntry> { currentLibrary, newLibrary });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormHandler> { staleHandler }, new HashSet<FormLibraryEntry> { currentLibrary })));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: true);

        var written = GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad);
        Assert.DoesNotContain(staleHandler, written); // stale handler's removal is safe — written
        Assert.DoesNotContain(newHandler, written); // new handler's library isn't on the form yet — deferred

        var writtenLibraries = GetLibrariesFromCapturedXml(captured, formId);
        Assert.DoesNotContain(newLibrary, writtenLibraries); // new library reference deferred too
    }

    [Fact]
    public async Task ExecuteAsync_CleanupOnlyDesiredHandlerAlreadyCurrentWithChangedParameters_UpdateIsWritten()
    {
        var formId = Guid.NewGuid();
        var currentHandler = new FormHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "oldParams");
        var desiredHandler = currentHandler with { Parameters = "newParams" };
        var library = new FormLibraryEntry("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new HashSet<FormHandler> { desiredHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibraryEntry> { library });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormHandler> { currentHandler }, new HashSet<FormLibraryEntry> { library })));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: true);

        var written = GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad);
        var writtenHandler = Assert.Single(written);
        Assert.Equal("newParams", writtenHandler.Parameters); // library already on the form — safe to apply now
    }
}
