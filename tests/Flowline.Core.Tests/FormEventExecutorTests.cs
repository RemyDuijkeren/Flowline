using System.ServiceModel;
using System.Xml.Linq;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
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

    static IReadOnlyList<FormEventHandler> GetHandlersFromCapturedXml(Dictionary<Guid, string> captured, Guid formId, FormEventType evt) =>
        FormXmlEventSerializer.GetHandlers(XDocument.Parse(captured[formId]), evt);

    static IReadOnlySet<FormLibrary> GetLibrariesFromCapturedXml(Dictionary<Guid, string> captured, Guid formId) =>
        FormXmlEventSerializer.GetLibraries(XDocument.Parse(captured[formId]));

    [Fact]
    public async Task ExecuteAsync_NoUnrecognizedHandlers_NoConfirmationPromptUpdatesApplyDirectly()
    {
        var formId = Guid.NewGuid();
        var handler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var library = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary> { library });

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
        var onLoadHandler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var onSaveHandler = new FormEventHandler("onSave", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnSave, "onSave", "av_/lib.js"), "");
        var library = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var onLoadPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { onLoadHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary> { library });
        var onSavePlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnSave,
            new List<FormEventHandler> { onSaveHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary> { library });

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
    public async Task ExecuteAsync_FormWithTwoOnChangeAttributePlans_MergedIntoSingleUpdateNeitherAttributeClobbered()
    {
        // Regression: FormEventFormPlan.Attribute must be threaded into GetHandlers/SetHandlers — without
        // it, two onchange attributes on one form would read/write the same (first-found) <event
        // name="onchange"> element and clobber each other.
        var formId = Guid.NewGuid();
        var creditLimitHandler = new FormEventHandler("onCreditlimitChange", "av_/lib.js",
            FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnChange, "onCreditlimitChange", "av_/lib.js", "creditlimit"), "");
        var revenueHandler = new FormEventHandler("onRevenueChange", "av_/lib.js",
            FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnChange, "onRevenueChange", "av_/lib.js", "revenue"), "");
        var library = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var creditLimitPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnChange,
            new List<FormEventHandler> { creditLimitHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary> { library }, "creditlimit");
        var revenuePlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnChange,
            new List<FormEventHandler> { revenueHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary> { library }, "revenue");

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(creditLimitPlan, revenuePlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        await _serviceMock.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        var capturedXdoc = XDocument.Parse(captured[formId]);
        Assert.Contains(creditLimitHandler, FormXmlEventSerializer.GetHandlers(capturedXdoc, FormEventType.OnChange, "creditlimit"));
        Assert.Contains(revenueHandler, FormXmlEventSerializer.GetHandlers(capturedXdoc, FormEventType.OnChange, "revenue"));
    }

    [Fact]
    public async Task ExecuteAsync_DryRunTwoOnChangeAttributesSharingFunctionAndLibrary_ChangeReportDistinguishesByAttribute()
    {
        // Code-review regression: HandlerChange previously carried no Attribute dimension, so two onchange
        // attributes sharing a FunctionName+LibraryName (a real scenario — the same handler can wire to
        // multiple fields) rendered as indistinguishable lines in the verbose/dry-run change report, even
        // though the actual formxml write was always correctly attribute-scoped.
        var formId = Guid.NewGuid();
        var creditLimitHandler = new FormEventHandler("sharedHandler", "av_/lib.js",
            FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnChange, "sharedHandler", "av_/lib.js", "creditlimit"), "");
        var revenueHandler = new FormEventHandler("sharedHandler", "av_/lib.js",
            FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnChange, "sharedHandler", "av_/lib.js", "revenue"), "");

        var creditLimitPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnChange,
            new List<FormEventHandler> { creditLimitHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>(), "creditlimit");
        var revenuePlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnChange,
            new List<FormEventHandler> { revenueHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>(), "revenue");

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(creditLimitPlan, revenuePlan);

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: true, cleanupOnly: false);

        Assert.Contains("OnChange:creditlimit", _console.Output);
        Assert.Contains("OnChange:revenue", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_UnrecognizedHandlersInteractiveConfirms_ProceedsHandlersRemoved()
    {
        var formId = Guid.NewGuid();
        var recognized = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var unrecognized = new FormEventHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        const string proposedAnnotation = "// flowline:onload account \"Account Main\" manualFn";
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { recognized, unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, proposedAnnotation) },
            new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        _console.Interactive();
        _console.Input.PushTextWithEnter("y");

        var savedCiVars = SaveAndClearCiVars();
        try
        {
            await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);
        }
        finally { RestoreCiVars(savedCiVars); }

        var written = GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad);
        Assert.Contains(recognized, written);
        Assert.DoesNotContain(unrecognized, written);
        Assert.Contains(proposedAnnotation, _console.Output); // R18a: proposed annotation shown in the interactive prompt too
    }

    [Fact]
    public async Task ExecuteAsync_UnrecognizedHandlersInteractiveDeclines_OnlyThatRemovalExcludedOtherActionsApply()
    {
        var formIdA = Guid.NewGuid();
        var existingRecognized = new FormEventHandler("onLoad", "av_/keep.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js"), "");
        var newRecognized = new FormEventHandler("onSave", "av_/keep.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onSave", "av_/keep.js"), "");
        var unrecognized = new FormEventHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { existingRecognized, newRecognized, unrecognized },
            new HashSet<UnrecognizedHandler> { new(unrecognized, "") },
            new HashSet<FormLibrary>());

        // Second, unrelated form with no unrecognized handlers — must still be updated regardless of the
        // decline decision on formPlanA.
        var formIdB = Guid.NewGuid();
        var contactHandler = new FormEventHandler("onLoad", "av_/contact.js", FormEventDeterministicId.ForHandler("contact", "Contact Main", FormEventType.OnLoad, "onLoad", "av_/contact.js"), "");
        var formPlanB = new FormEventFormPlan(formIdB, "contact", "Contact Main", FormEventType.OnLoad,
            new List<FormEventHandler> { contactHandler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(
            new DataverseForm(formIdA, "Account Main", "account", BuildFormXml()),
            new DataverseForm(formIdB, "Contact Main", "contact", BuildFormXml()));
        var plan = BuildPlan(formPlanA, formPlanB);
        var captured = CaptureUpdatedFormXml();

        _console.Interactive();
        _console.Input.PushTextWithEnter("n");

        var savedCiVars = SaveAndClearCiVars();
        try
        {
            await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);
        }
        finally { RestoreCiVars(savedCiVars); }

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
        var unrecognized = new FormEventHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        const string proposedAnnotation = "// flowline:onload account \"Account Main\" manualFn";
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, proposedAnnotation) },
            new HashSet<FormLibrary>());

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
        var recognized = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var unrecognized = new FormEventHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { recognized, unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, "") },
            new HashSet<FormLibrary>());

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
        var handlerA = new FormEventHandler("onLoad", "av_/a.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/a.js"), "");
        var handlerB = new FormEventHandler("onLoad", "av_/b.js", FormEventDeterministicId.ForHandler("account", "Account QuickCreate", FormEventType.OnLoad, "onLoad", "av_/b.js"), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerA }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());
        var formPlanB = new FormEventFormPlan(formIdB, "account", "Account QuickCreate", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerB }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

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
    public async Task ExecuteAsync_PublishAfterSyncFalse_UpdatesStillWrittenButNoPublishXmlCall()
    {
        // --no-publish should extend to form-event handler changes too: the formxml write still happens
        // (handlers are saved), only PublishXml is skipped -- the caller decides when it goes live.
        var formId = Guid.NewGuid();
        var handler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false, publishAfterSync: false);

        Assert.Contains(handler, GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad));
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_TwoDifferentEntities_TwoSeparatePublishXmlCalls()
    {
        var formIdA = Guid.NewGuid();
        var formIdB = Guid.NewGuid();
        var handlerA = new FormEventHandler("onLoad", "av_/a.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/a.js"), "");
        var handlerB = new FormEventHandler("onLoad", "av_/b.js", FormEventDeterministicId.ForHandler("contact", "Contact Main", FormEventType.OnLoad, "onLoad", "av_/b.js"), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerA }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());
        var formPlanB = new FormEventFormPlan(formIdB, "contact", "Contact Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerB }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

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
        var handlerFails = new FormEventHandler("onLoad", "av_/fails.js", FormEventDeterministicId.ForHandler("account", "Account Fails", FormEventType.OnLoad, "onLoad", "av_/fails.js"), "");
        var handlerOk = new FormEventHandler("onLoad", "av_/ok.js", FormEventDeterministicId.ForHandler("account", "Account Ok", FormEventType.OnLoad, "onLoad", "av_/ok.js"), "");
        var formPlanFails = new FormEventFormPlan(formIdFails, "account", "Account Fails", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerFails }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());
        var formPlanOk = new FormEventFormPlan(formIdOk, "account", "Account Ok", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerOk }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

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

    // Regression: a live push crashed with "Could not find color or style 'Publish'" — the failure summary
    // interpolated ex.Message straight into Spectre markup, and a Dataverse fault message containing
    // "[Publish...]"-shaped text was parsed as a markup tag instead of printed literally.
    [Fact]
    public async Task ExecuteAsync_FailureMessageContainsBracketedText_ReportedLiterallyNotParsedAsMarkup()
    {
        var formId = Guid.NewGuid();
        var handler = new FormEventHandler("onLoad", "av_/fails.js", FormEventDeterministicId.ForHandler("account", "Account Fails", FormEventType.OnLoad, "onLoad", "av_/fails.js"), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Fails", FormEventType.OnLoad,
            new List<FormEventHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Fails", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);

        _serviceMock.UpdateAsync(Arg.Is<Entity>(e => e.Id == formId), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault(), "operation [Publish] failed unexpectedly")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false));

        Assert.Contains("1 form event operation(s) failed", ex.Message);
        Assert.Contains("operation [Publish] failed unexpectedly", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_PublishXmlFailureForOneEntity_SurfacedAsHardFailureOtherEntitiesStillAttempt()
    {
        var formIdA = Guid.NewGuid();
        var formIdB = Guid.NewGuid();
        var handlerA = new FormEventHandler("onLoad", "av_/a.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/a.js"), "");
        var handlerB = new FormEventHandler("onLoad", "av_/b.js", FormEventDeterministicId.ForHandler("contact", "Contact Main", FormEventType.OnLoad, "onLoad", "av_/b.js"), "");
        var formPlanA = new FormEventFormPlan(formIdA, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerA }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());
        var formPlanB = new FormEventFormPlan(formIdB, "contact", "Contact Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerB }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

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
    public async Task ExecuteAsync_PublishXmlFails_MessageDistinguishesSavedFromPublished()
    {
        // The formxml write already succeeded when publish faults - the message must say so clearly
        // (not just dump the raw fault), since Dataverse exposes no queryable "needs publish" signal
        // for systemform to catch this on a later run.
        var formId = Guid.NewGuid();
        var handler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        CaptureUpdatedFormXml();

        _serviceMock.ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>())
            .Returns<OrganizationResponse>(_ => throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "publish contention"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false));

        Assert.Contains("saved, but the publish that makes them live failed", _console.Output);
        Assert.Contains("not yet published", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_FormHasRowVersion_UpdatesWithOptimisticConcurrency()
    {
        // Confirmed live: systemform has IsOptimisticConcurrencyEnabled=true. When the snapshot carries
        // a RowVersion (always true for a real Dataverse read), the write must use ConcurrencyBehavior
        // .IfRowVersionMatches, not a plain unconditional UpdateAsync.
        var formId = Guid.NewGuid();
        var handler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml(), RowVersion: "12345"));
        var plan = BuildPlan(formPlan);

        _serviceMock.ExecuteAsync(Arg.Is<UpdateRequest>(r => r.Target.Id == formId), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UpdateResponse() as OrganizationResponse));

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<UpdateRequest>(r => r.Target.Id == formId && r.Target.RowVersion == "12345" && r.ConcurrencyBehavior == ConcurrencyBehavior.IfRowVersionMatches),
            Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyVersionMismatch_SurfacesActionableMessageNotRawFault()
    {
        var formId = Guid.NewGuid();
        var handler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml(), RowVersion: "12345"));
        var plan = BuildPlan(formPlan);

        _serviceMock.ExecuteAsync(Arg.Is<UpdateRequest>(r => r.Target.Id == formId), Arg.Any<CancellationToken>())
            .Returns<OrganizationResponse>(_ => throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = -2147088254 }, "The version of the existing record doesn't match the RowVersion property provided."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false));

        Assert.Contains("1 form event operation(s) failed", ex.Message);
        Assert.Contains("modified since Flowline last read it", _console.Output);
        Assert.Contains("re-run push", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunWithUnrecognizedHandlers_PrintsDetailAndAnnotationsWritesNothingNoPrompt()
    {
        var formId = Guid.NewGuid();
        var recognized = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var unrecognized = new FormEventHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        const string proposedAnnotation = "// flowline:onload account \"Account Main\" manualFn";
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { recognized, unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, proposedAnnotation) },
            new HashSet<FormLibrary>());

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
    public async Task ExecuteAsync_DryRunWithForceAndUnrecognizedHandlers_ReportsRemovalMatchingRealForceRun()
    {
        // Regression: --dry-run --force previously always reported unrecognized handlers as kept
        // (removeUnrecognized hardcoded to false in the preview), even though a real --force run
        // would actually remove them — an automation script previewing an unattended force-push got
        // an inaccurate, undercounted picture of what would change.
        var formId = Guid.NewGuid();
        var recognized = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var unrecognized = new FormEventHandler("manualFn", "av_/manual.js", Guid.NewGuid(), "");
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { recognized, unrecognized }, new HashSet<UnrecognizedHandler> { new(unrecognized, "") },
            new HashSet<FormLibrary>());

        // Unrecognized handler is already present on the form's current formxml (the realistic R18
        // shape: manually added, not Flowline-created) so excluding it from desired under force shows
        // up as an actual removal, not merely "never added".
        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { unrecognized })));
        var plan = BuildPlan(formPlan);

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: true, dryRun: true, cleanupOnly: false);

        Assert.Contains("1 handler(s) removed", _console.Output);
        Assert.Contains("manualFn", _console.Output);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DryRunWithParametersOnlyChange_ReportedAsUpdatedNotSilentlyDropped()
    {
        // FormEventHandler's identity equality ignores Parameters, so a Parameters-only change is present in
        // both "desired" and "current" by identity — the summary must not let that fall through the
        // added/removed counts as if nothing changed (R18b: the preview must be honest about updates too).
        var formId = Guid.NewGuid();
        var currentHandler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "oldParams");
        var desiredHandler = currentHandler with { Parameters = "newParams" };
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { desiredHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { currentHandler })));
        var plan = BuildPlan(formPlan);

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: true, cleanupOnly: false);

        Assert.Contains("1 handler(s) updated", _console.Output);
        Assert.Contains("Handlers updated (1)", _console.Output);
        Assert.Contains("onLoad", _console.Output);
        Assert.Contains("av_/lib.js", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunLibraryOnlyChange_PrintsLibraryDetailNotJustZeroHandlerCounts()
    {
        // Regression: a library-only change (no handler add/update/remove — e.g. an orphaned library with
        // its last handler already gone) previously reported as "0 handler(s) would be added, 0 updated,
        // 0 removed" with no indication anything was pending at all: the library change itself was
        // invisible in --dry-run. Also covers the underlying complaint that --verbose showed no more detail
        // than a bare form count, unlike WebResources/Plugins.
        var formId = Guid.NewGuid();
        var orphanLibrary = new FormLibrary("av_/orphan.js", FormEventDeterministicId.ForLibrary("av_/orphan.js"));
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler>(), new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler>(), new HashSet<FormLibrary> { orphanLibrary })));
        var plan = BuildPlan(formPlan);

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: true, cleanupOnly: false);

        Assert.Contains("1 library(ies) removed", _console.Output);
        Assert.Contains("Libraries removed (1)", _console.Output);
        Assert.Contains("account/Account Main: av_/orphan.js", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_NotDryRun_PrintsSamePerHandlerDetailAsDryRunAfterApplying()
    {
        // The detail report isn't dry-run-only — a real (non-dry-run) push should show exactly what got
        // applied, same as WebResourceService's verbose plan report. Reflects what was actually written
        // (post-execution), not the pre-narrowing plan — the single source of truth is BuildFormXml's own
        // change list, not a separate preview computation that could drift from it.
        var formId = Guid.NewGuid();
        var handler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "");
        var library = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handler }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary> { library });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        Assert.Contains("Handlers added (1)", _console.Output);
        Assert.Contains("account/Account Main (OnLoad): onLoad (av_/lib.js)", _console.Output);
        Assert.Contains("Libraries added (1)", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupOnlyMixOfNewAndStaleHandler_OnlyStaleRemovalWrittenNewExcluded()
    {
        var formId = Guid.NewGuid();
        var staleHandler = new FormEventHandler("legacyFn", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "legacyFn", "av_/lib.js"), "");
        var newHandler = new FormEventHandler("onLoad", "av_/new.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/new.js"), "");
        var newLibrary = new FormLibrary("av_/new.js", FormEventDeterministicId.ForLibrary("av_/new.js"));
        var currentLibrary = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        // Planner already excludes a stale-but-deterministic-id handler from DesiredHandlers entirely (it's
        // simply not re-added on write) — only the new handler is in Desired here. staleHandler exists only
        // on the current form's formxml below.
        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { newHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibrary> { currentLibrary, newLibrary });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { staleHandler }, new HashSet<FormLibrary> { currentLibrary })));
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
    public async Task ExecuteAsync_CleanupOnlyRemovesStaleHandler_PrintsAlwaysVisibleSummaryAndVerboseDetail()
    {
        // User-requested: cleanup must show what it's deleting — at least an always-visible summary
        // (console.Ok, not gated by -v), and the full per-item breakdown under --verbose. Previously
        // cleanup's report was suppressed entirely (reasoning it would duplicate registration's report),
        // which left a genuine removal completely silent — indistinguishable from an apparent no-op hang.
        var formId = Guid.NewGuid();
        var staleHandler = new FormEventHandler("legacyFn", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "legacyFn", "av_/lib.js"), "");
        var staleLibrary = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler>(), new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary>());

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { staleHandler }, new HashSet<FormLibrary> { staleLibrary })));
        var plan = BuildPlan(formPlan);
        CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: true);

        // Always-visible summary (console.Ok) — not just verbose.
        Assert.Contains("1 handler(s) removed", _console.Output);
        Assert.Contains("1 library(ies) removed", _console.Output);
        // Verbose per-item detail — TestConsole has no VerboseFilterHook attached, so console.Verbose
        // output is captured unfiltered here (see FormEventReaderTests/SubprocessCaptureTests for the same
        // pattern), letting this assert the detail exists at all rather than its terminal visibility.
        Assert.Contains("Handlers removed (1)", _console.Output);
        Assert.Contains("account/Account Main (OnLoad): legacyFn (av_/lib.js)", _console.Output);
        Assert.Contains("Libraries removed (1)", _console.Output);
        Assert.Contains("account/Account Main: av_/lib.js", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupOnlyDesiredHandlerAlreadyCurrentWithChangedParameters_UpdateDeferredNothingWritten()
    {
        // Cleanup is removals-only (clean separation of concerns) — a parameter-only change is an update,
        // not a removal, so it's deferred to registration even though the handler's library is already
        // safely on the form and applying it now would have been technically safe.
        var formId = Guid.NewGuid();
        var currentHandler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "oldParams");
        var desiredHandler = currentHandler with { Parameters = "newParams" };
        var library = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { desiredHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibrary> { library });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { currentHandler }, new HashSet<FormLibrary> { library })));
        var plan = BuildPlan(formPlan);

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: true);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegistrationDesiredHandlerAlreadyCurrentWithChangedParameters_UpdateIsWritten()
    {
        // Same scenario as the cleanup-deferral test above, but for the registration (cleanupOnly: false)
        // pass — this is where a parameter-only update actually gets applied.
        var formId = Guid.NewGuid();
        var currentHandler = new FormEventHandler("onLoad", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js"), "oldParams");
        var desiredHandler = currentHandler with { Parameters = "newParams" };
        var library = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { desiredHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibrary> { library });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account",
            BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { currentHandler }, new HashSet<FormLibrary> { library })));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        var written = GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad);
        var writtenHandler = Assert.Single(written);
        Assert.Equal("newParams", writtenHandler.Parameters);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupOnlyPurelyAdditiveChange_SkipsUpdateAndPublishEntirely()
    {
        // Regression: reported live as an apparent double-publish — a form whose ONLY pending change is a
        // brand-new handler+library (nothing to clean up) still had UpdateAsync and PublishXml called
        // during the cleanup pass, even though cleanupOnly's narrowing (BuildFormXml) excludes the whole
        // change and writes back formxml identical to the current state. Confirmed via the progress bar
        // hitting 100% twice for the same push. The cleanup pass must skip both entirely when nothing was
        // actually written.
        var formId = Guid.NewGuid();
        var newHandler = new FormEventHandler("onLoad", "av_/new.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/new.js"), "");
        var newLibrary = new FormLibrary("av_/new.js", FormEventDeterministicId.ForLibrary("av_/new.js"));

        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { newHandler }, new HashSet<UnrecognizedHandler>(),
            new HashSet<FormLibrary> { newLibrary });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: true);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
        // Regression: nothing to clean up must print nothing at all — no progress bar, no "no changes"
        // summary line — rather than rendering a "Cleaning forms" bar that jumps straight to 100% for
        // zero real work (confusing, reads as a hang or a no-op mistaken for done).
        Assert.Equal("", _console.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleHandlersInPlan_WritesInExactSuppliedOrderNotAlphabeticalOrIdentityOrder()
    {
        // KTD2/KTD8 regression guard: the planner's computed handler order (here deliberately
        // non-alphabetical) must survive the executor's de-duplication step (FormEventExecutor.cs's
        // desired.Distinct().ToList()) all the way to the written FormXml. Assert.Contains alone (used by
        // every other executor test) can't catch a reordering regression — only a sequence assertion can.
        var formId = Guid.NewGuid();
        var handlerC = new FormEventHandler("Charlie", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "Charlie", "av_/lib.js"), "");
        var handlerA = new FormEventHandler("Alpha", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "Alpha", "av_/lib.js"), "");
        var handlerB = new FormEventHandler("Bravo", "av_/lib.js", FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "Bravo", "av_/lib.js"), "");
        var library = new FormLibrary("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js"));

        var formPlan = new FormEventFormPlan(formId, "account", "Account Main", FormEventType.OnLoad,
            new List<FormEventHandler> { handlerC, handlerA, handlerB }, new HashSet<UnrecognizedHandler>(), new HashSet<FormLibrary> { library });

        var snapshot = BuildSnapshot(new DataverseForm(formId, "Account Main", "account", BuildFormXml()));
        var plan = BuildPlan(formPlan);
        var captured = CaptureUpdatedFormXml();

        await _executor.ExecuteAsync(_serviceMock, snapshot, plan, force: false, dryRun: false, cleanupOnly: false);

        var written = GetHandlersFromCapturedXml(captured, formId, FormEventType.OnLoad);
        Assert.Equal(["Charlie", "Alpha", "Bravo"], written.Select(h => h.FunctionName));
    }
}
