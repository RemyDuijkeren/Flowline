using System.Xml.Linq;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core.Services.FormEvents;
using Flowline.Core.Services.FormEvents.Support;
using Spectre.Console.Testing;
using static Flowline.Core.Tests.FormEventTestHelpers;

namespace Flowline.Core.Tests;

public class FormEventPlannerTests
{
    readonly TestConsole _console = new();
    readonly FormEventPlanner _planner;

    public FormEventPlannerTests() => _planner = new FormEventPlanner(_console);

    // Tracked-library set (R15) is auto-inferred from every library referenced by an annotation or a
    // current Handler/Library already on a passed-in form — matches the pre-U5-revision behavior where
    // everything was implicitly "tracked" (no boundary existed), so existing scenarios need no changes.
    // Use BuildSnapshotUntrackedLibrary for a test that specifically needs a library excluded.
    static FormEventSnapshot BuildSnapshot(
        IReadOnlyList<ResolvedFormEventAnnotation> annotations,
        params (string Entity, string Form, DataverseForm Form2)[] forms)
    {
        var dict = forms.ToDictionary(f => (f.Entity, f.Form), f => f.Form2);

        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in annotations)
            tracked.Add(a.LibraryName);
        foreach (var (_, _, dataverseForm) in forms)
        {
            var xdoc = XDocument.Parse(dataverseForm.FormXml);
            foreach (var evt in Enum.GetValues<FormEventType>())
                foreach (var h in FormXmlEventSerializer.GetHandlers(xdoc, evt))
                    tracked.Add(h.LibraryName);
            foreach (var l in FormXmlEventSerializer.GetLibraries(xdoc))
                tracked.Add(l.Name);
        }

        return new FormEventSnapshot(annotations, tracked, dict);
    }

    // Same as BuildSnapshot, but explicitly excludes one library name from the auto-inferred tracked
    // set — for R15 regression tests where a current Handler must land on a library this project does
    // not track.
    static FormEventSnapshot BuildSnapshotUntrackedLibrary(
        IReadOnlyList<ResolvedFormEventAnnotation> annotations,
        string untrackedLibraryName,
        params (string Entity, string Form, DataverseForm Form2)[] forms)
    {
        var snapshot = BuildSnapshot(annotations, forms);
        var tracked = new HashSet<string>(snapshot.TrackedLibraryNames, StringComparer.OrdinalIgnoreCase);
        tracked.Remove(untrackedLibraryName);
        return new FormEventSnapshot(snapshot.Annotations, tracked, snapshot.Forms);
    }

    [Fact]
    public void Plan_DesiredMatchesCurrentExactlyNoUnrecognized_ProducesNoPlanEntry()
    {
        var handlerId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var current = new HashSet<FormEventHandler> { new("onLoad", "av_/lib.js", handlerId, "") };
        var libraries = new HashSet<FormLibrary> { new("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js")) };
        var formXml = BuildFormXml(FormEventType.OnLoad, current, libraries);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onLoad() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        Assert.Empty(plan.Forms);
    }

    [Fact]
    public void Plan_NewAnnotationNoExistingEvents_ProducesEntryWithHandlerAndLibrary()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onLoad() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        var expectedId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "onLoad" && h.LibraryName == "av_/lib.js" && h.HandlerUniqueId == expectedId);
        Assert.Single(entry.DesiredLibraries, l => l.Name == "av_/lib.js");
        Assert.Empty(entry.UnrecognizedHandlers);
    }

    [Fact]
    public void Plan_OrphanedHandlerWithMatchingDeterministicId_IsRemoved()
    {
        var keptId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js");
        var orphanId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "orphanFn", "av_/orphan.js");
        var current = new HashSet<FormEventHandler>
        {
            new("onLoad", "av_/keep.js", keptId, ""),
            new("orphanFn", "av_/orphan.js", orphanId, "")
        };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/keep.js", "function onLoad() {}", "src/keep.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.DoesNotContain(entry.DesiredHandlers, h => h.FunctionName == "orphanFn");
        Assert.Contains(entry.DesiredHandlers, h => h.FunctionName == "onLoad");
        Assert.Empty(entry.UnrecognizedHandlers);
    }

    [Fact]
    public void Plan_OrphanedHandlerWithNonMatchingId_IsKeptAndFlaggedUnrecognized()
    {
        var keptId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js");
        var current = new HashSet<FormEventHandler>
        {
            new("onLoad", "av_/keep.js", keptId, ""),
            new("manualFn", "av_/manual.js", Guid.NewGuid(), "")
        };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/keep.js", "function onLoad() {}", "src/keep.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Contains(entry.DesiredHandlers, h => h.FunctionName == "manualFn");
        Assert.Contains(entry.UnrecognizedHandlers, u => u.Handler.FunctionName == "manualFn");
    }

    [Fact]
    public void Plan_HandlerOnUntrackedLibrary_NeverTouchedNeverFlaggedUnrecognized()
    {
        // R15 regression (KTD11): a Handler whose library isn't tracked by this project is never
        // evaluated at all — not eligible for stale auto-removal, never surfaced as unrecognized, passed
        // through untouched so the write-back doesn't drop it. Replaces the old (incorrect) behavior
        // this test used to document, where an untracked-library handler was flagged unrecognized just
        // like a tracked one with a non-matching id.
        var keptId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js");
        var current = new HashSet<FormEventHandler>
        {
            new("onLoad", "av_/keep.js", keptId, ""),
            new("untrackedFn", "av_/untracked.js", Guid.NewGuid(), "")
        };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/keep.js", "function onLoad() {}", "src/keep.ts");

        var snapshot = BuildSnapshotUntrackedLibrary([resolved], "av_/untracked.js", ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Contains(entry.DesiredHandlers, h => h.FunctionName == "untrackedFn" && h.LibraryName == "av_/untracked.js");
        Assert.DoesNotContain(entry.UnrecognizedHandlers, u => u.Handler.FunctionName == "untrackedFn");
        // The untracked/foreign handler's library was never declared in <formLibraries> on this form
        // (BuildFormXml was called with no libraries) — reconciling it in would fabricate a Library entry
        // for a handler Flowline doesn't manage.
        Assert.DoesNotContain(entry.DesiredLibraries, l => l.Name == "av_/untracked.js");
    }

    [Fact]
    public void Plan_ForeignHandlerLibraryUndeclaredInFormLibraries_NeverAddedNeverProducesPlanEntry()
    {
        // Regression: a live push crashed trying to publish "contact" even though no annotation ever
        // referenced that entity. Root cause — a pre-existing Dataverse OOB Handler (e.g. a Sales module
        // script) referenced a library that was never declared in <formLibraries> (registered some other
        // way, e.g. <internaljscriptfile>). The old neededLibraryNames computation unioned ALL desired
        // handlers' library names, including foreign pass-through ones, so it "fixed" the undeclared
        // library by fabricating a new <Library> entry with a Flowline-derived id — flipping
        // librariesChangedForForm to true and producing a spurious plan entry (via the narrow fallback)
        // for a form/entity this project was never asked to touch at all.
        var foreignHandler = new FormEventHandler("thirdParty.onLoad", "thirdparty/lib.js", Guid.NewGuid(), "");
        var current = new HashSet<FormEventHandler> { foreignHandler };
        // No libraries declared at all — foreignHandler's library is referenced by the handler but absent
        // from <formLibraries>, mirroring the live Dataverse OOB form exactly.
        var formXml = BuildFormXml(FormEventType.OnLoad, current, new HashSet<FormLibrary>());
        var form = new DataverseForm(Guid.NewGuid(), "Contact Main", "contact", formXml);

        // No annotation ever targets this form/entity.
        var snapshot = BuildSnapshotUntrackedLibrary([], "thirdparty/lib.js", ("contact", "Contact Main", form));

        var plan = _planner.Plan(snapshot);

        Assert.Empty(plan.Forms);
    }

    [Fact]
    public void Plan_FlowlineOwnedHandlerOnJustDeletedLibrary_StillRecognizedAndRemovedNotCarriedThroughAsForeign()
    {
        // R14 regression: deleting a JS file entirely removes it from TrackedLibraryNames (built from
        // currently-existing local files, not history) — this must NOT reclassify a Flowline-created
        // handler on that library as "foreign" and carry it through untouched, or the cleanup phase never
        // clears the reference and the subsequent web resource DeleteAsync hits the exact Dataverse
        // dependency-fault KTD12 exists to prevent. The deterministic ID match (proof of Flowline
        // ownership, independent of current tracked-status) must be checked before the tracked-library
        // gate, not after.
        var handlerId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/deleted.js");
        var current = new HashSet<FormEventHandler> { new("onLoad", "av_/deleted.js", handlerId, "") };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        // No annotation anywhere for av_/deleted.js (its source file is gone) — and it's explicitly
        // excluded from the tracked set, simulating "no longer present on local disk".
        var snapshot = BuildSnapshotUntrackedLibrary([], "av_/deleted.js", ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Empty(entry.DesiredHandlers); // dropped — NOT carried through as foreign
        Assert.Empty(entry.UnrecognizedHandlers);
        // Correctness-review regression: the <Library> entry itself must also leave DesiredLibraries once
        // its last handler is cleanly auto-removed as stale — otherwise the web resource still looks
        // referenced via <formLibraries>, and its delete still faults even though the <Handler> was cleaned.
        Assert.Empty(entry.DesiredLibraries);
    }

    [Fact]
    public void Plan_LibraryStillNeededByOtherEvent_NotRemovedEvenWhenThisEventsHandlerWasOrphaned()
    {
        // Libraries are form-level (<formLibraries> is shared by onLoad and onSave), not per-event — a
        // library must NOT be dropped just because ONE event's handler on it was cleanly removed if the
        // OTHER event still has a surviving handler referencing the same library.
        var staleOnLoadId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/shared.js");
        var currentOnLoad = new HashSet<FormEventHandler> { new("onLoad", "av_/shared.js", staleOnLoadId, "") };
        var currentOnSave = new List<FormEventHandler> { new("onSave", "av_/shared.js", Guid.NewGuid(), "") }; // kept via annotation below

        var xdoc = System.Xml.Linq.XDocument.Parse(BuildFormXml(FormEventType.OnLoad, currentOnLoad));
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnSave, currentOnSave);
        FormXmlEventSerializer.SetLibraries(xdoc, new HashSet<FormLibrary> { new("av_/shared.js", Guid.NewGuid()) });
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        // No annotation for onLoad (its handler is now orphaned and stale) — but a live annotation for
        // onSave keeps that event's handler, and therefore the shared library, in place.
        var onSaveAnnotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnSave, "onSave", null);
        var onSaveResolved = new ResolvedFormEventAnnotation(onSaveAnnotation, "av_/shared.js", "function onSave() {}", "src/shared.ts");

        var snapshot = BuildSnapshot([onSaveResolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var onLoadEntry = Assert.Single(plan.Forms, e => e.Event == FormEventType.OnLoad);
        Assert.Empty(onLoadEntry.DesiredHandlers); // stale onLoad handler dropped
        Assert.Contains(onLoadEntry.DesiredLibraries, l => l.Name == "av_/shared.js"); // library kept — onSave still needs it
    }

    [Fact]
    public void Plan_FormWithHandlerAndZeroAnnotationsAnywhere_StillEvaluatedOrphanHandlerRemoved()
    {
        // R14 regression (KTD11): no annotation anywhere references this form/event — only reachable by
        // iterating snapshot.Forms (the full solution-scoped set), not by grouping snapshot.Annotations.
        var handlerId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "legacyOnLoad", "av_/legacy.js");
        var current = new HashSet<FormEventHandler> { new("legacyOnLoad", "av_/legacy.js", handlerId, "") };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var snapshot = BuildSnapshot([], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Empty(entry.DesiredHandlers);
        Assert.Empty(entry.UnrecognizedHandlers);
    }

    [Fact]
    public void Plan_UnrecognizedHandlerWithDottedFunctionName_ProposedAnnotationUsesDottedNameVerbatim()
    {
        var keptId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js");
        var current = new HashSet<FormEventHandler>
        {
            new("onLoad", "av_/keep.js", keptId, ""),
            new("MyCompany.Example1.OnLoad", "av_/legacy.js", Guid.NewGuid(), "")
        };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/keep.js", "function onLoad() {}", "src/keep.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        var unrecognized = Assert.Single(entry.UnrecognizedHandlers);
        Assert.Equal("// flowline:onload account \"Account Main\" MyCompany.Example1.OnLoad", unrecognized.ProposedAnnotation);
    }

    [Fact]
    public void Plan_UnrecognizedHandlerWithParameters_ProposedAnnotationIncludesParameterSuffix()
    {
        var keptId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js");
        var current = new HashSet<FormEventHandler>
        {
            new("onLoad", "av_/keep.js", keptId, ""),
            new("legacyFn", "av_/legacy.js", Guid.NewGuid(), "param1,param2")
        };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/keep.js", "function onLoad() {}", "src/keep.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        var unrecognized = Assert.Single(entry.UnrecognizedHandlers);
        Assert.Equal("// flowline:onload account \"Account Main\" legacyFn(param1,param2)", unrecognized.ProposedAnnotation);
    }

    [Fact]
    public void Plan_DefaultedFunctionNotFound_ThrowsRegardlessOfConfidence()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        // Defaulted (FunctionName omitted) and content with no parseable exports/declarations at all
        // (Confident: false) — R7 hard-fails on ANY non-resolution regardless of Confident.
        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/empty.js", "// nothing here", "src/empty.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("src/empty.ts", ex.Message);
        Assert.Contains("onLoad", ex.Message);
    }

    [Fact]
    public void Plan_ExplicitFunctionConfirmedAbsent_ThrowsButOtherAnnotationsStillApply()
    {
        // R7a outcome 2: explicit function name, confirmed absent from a fully-traced known export shape
        // (Found:false, Confident:true) — hard-fails naming file+function; other valid registrations in
        // the same push still apply (per-declaration failure, same isolation as R7).
        var formXmlGood = BuildFormXml();
        var formGood = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXmlGood);
        var goodAnnotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var goodResolved = new ResolvedFormEventAnnotation(goodAnnotation, "av_/good.js", "function onLoad() {}", "src/good.ts");

        var formXmlBad = BuildFormXml();
        var formBad = new DataverseForm(Guid.NewGuid(), "Contact Main", "contact", formXmlBad);
        var badAnnotation = new FormEventAnnotation("contact", "Contact Main", FormEventType.OnLoad, "MissingFn", null);
        // Content has a real, fully-traceable top-level declaration (Confident: true) that just isn't
        // the requested name — distinguishes "confirmed absent" from "inconclusive" (empty/comment-only).
        var badResolved = new ResolvedFormEventAnnotation(badAnnotation, "av_/bad.js", "function otherFn() {}", "src/bad.ts");

        var snapshot = BuildSnapshot(
            [goodResolved, badResolved],
            ("account", "Account Main", formGood),
            ("contact", "Contact Main", formBad));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("src/bad.ts", ex.Message);
        Assert.Contains("MissingFn", ex.Message);
    }

    [Fact]
    public void Plan_ExplicitFunctionInconclusive_WarnsAndRegistersVerbatimNotHardFail()
    {
        // R7a outcome 3: explicit function name, genuinely unconfirmable (Found:false, Confident:false)
        // — warns, does not hard-fail, registers the name verbatim as the user wrote it.
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "LegacyHandler", null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/legacy.js", "// nothing here", "src/legacy.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "LegacyHandler" && h.LibraryName == "av_/legacy.js");
        Assert.Contains("could not be confirmed", _console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_UnresolvableFunctionInOneGroup_ThrowsButStillProcessesOtherGroups()
    {
        var formXmlGood = BuildFormXml();
        var formGood = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXmlGood);
        var goodAnnotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var goodResolved = new ResolvedFormEventAnnotation(goodAnnotation, "av_/good.js", "function onLoad() {}", "src/good.ts");

        var formXmlBad1 = BuildFormXml();
        var formBad1 = new DataverseForm(Guid.NewGuid(), "Contact Main", "contact", formXmlBad1);
        var badAnnotation1 = new FormEventAnnotation("contact", "Contact Main", FormEventType.OnLoad, "missingFn1", null);
        // Confident:true content (a real, unrelated top-level declaration) so this exercises R7a's
        // confirmed-absent hard-fail path, not the inconclusive warn-and-register path.
        var badResolved1 = new ResolvedFormEventAnnotation(badAnnotation1, "av_/bad1.js", "function otherFn1() {}", "src/bad1.ts");

        var formXmlBad2 = BuildFormXml();
        var formBad2 = new DataverseForm(Guid.NewGuid(), "Lead Main", "lead", formXmlBad2);
        var badAnnotation2 = new FormEventAnnotation("lead", "Lead Main", FormEventType.OnLoad, "missingFn2", null);
        var badResolved2 = new ResolvedFormEventAnnotation(badAnnotation2, "av_/bad2.js", "function otherFn2() {}", "src/bad2.ts");

        var snapshot = BuildSnapshot(
            [goodResolved, badResolved1, badResolved2],
            ("account", "Account Main", formGood),
            ("contact", "Contact Main", formBad1),
            ("lead", "Lead Main", formBad2));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        // Both failures are reported — proves processing of later groups isn't short-circuited by the first.
        Assert.Contains("src/bad1.ts", ex.Message);
        Assert.Contains("missingFn1", ex.Message);
        Assert.Contains("src/bad2.ts", ex.Message);
        Assert.Contains("missingFn2", ex.Message);
    }

    [Fact]
    public void Plan_ParametersChangedOnOtherwiseIdenticalHandler_ProducesPlanEntryWithNewParameters()
    {
        var handlerId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var current = new HashSet<FormEventHandler> { new("onLoad", "av_/lib.js", handlerId, "oldParams") };
        var libraries = new HashSet<FormLibrary> { new("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js")) };
        var formXml = BuildFormXml(FormEventType.OnLoad, current, libraries);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, "newParams");
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onLoad() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        var desired = Assert.Single(entry.DesiredHandlers);
        Assert.Equal("newParams", desired.Parameters);
        Assert.Equal(handlerId, desired.HandlerUniqueId);
    }

    [Fact]
    public void Plan_LibraryWithNoHandlerReferencingIt_RemovedRegardlessOfOwnership()
    {
        // Regression: a live push left av_Cr07982/example1.js declared in <formLibraries> with zero
        // handlers referencing it (its last handler — foreign, not Flowline-owned — had already been
        // removed outside this feature entirely). A <formLibraries><Library> entry with no Handler using
        // it does nothing functionally, so it's safe to drop regardless of who removed the last handler
        // or when — Flowline doesn't need an attributable reason, only "is anything still using it".
        var orphanLibrary = new FormLibrary("av_/orphan.js", FormEventDeterministicId.ForLibrary("av_/orphan.js"));
        var currentLibraries = new HashSet<FormLibrary> { orphanLibrary };
        var formXml = BuildFormXml(libraries: currentLibraries);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/newlib.js", "function onLoad() {}", "src/newlib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.DoesNotContain(entry.DesiredLibraries, l => l.Name == "av_/orphan.js");
        Assert.Contains(entry.DesiredLibraries, l => l.Name == "av_/newlib.js");
        Assert.Single(entry.DesiredLibraries);
    }

    [Fact]
    public void Plan_LibraryWithNoHandlerAndNoAnnotationChange_RemovedViaNarrowFallback()
    {
        // Same scenario, but with no annotation-driven change on the form at all — only reachable via the
        // planner's narrow library-only fallback (no event's handlersChanged/unrecognized flips true).
        var orphanLibrary = new FormLibrary("av_/orphan.js", FormEventDeterministicId.ForLibrary("av_/orphan.js"));
        var currentLibraries = new HashSet<FormLibrary> { orphanLibrary };
        var formXml = BuildFormXml(libraries: currentLibraries);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var snapshot = BuildSnapshot([], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Empty(entry.DesiredLibraries);
        Assert.Empty(entry.DesiredHandlers);
        Assert.Empty(entry.UnrecognizedHandlers);
    }

    [Fact]
    public void Plan_UntrackedLibraryWithNoHandlerReferencingIt_NeverRemovedOutsideProjectBoundary()
    {
        // A Microsoft/foreign library (never one of this project's own local JS files) that happens to have
        // zero current handlers referencing it — e.g. temporarily unwired for reasons outside Flowline's
        // knowledge — must never be touched. Only libraries inside this project's own tracked-library
        // boundary (R15) are eligible for cleanup; anything else is left alone regardless of reference
        // count, since Flowline can't be certain nothing else on the platform depends on it.
        var foreignLibrary = new FormLibrary("msdyn_/Account/AssetCommon.Account.Library.js",
            FormEventDeterministicId.ForLibrary("msdyn_/Account/AssetCommon.Account.Library.js"));
        var currentLibraries = new HashSet<FormLibrary> { foreignLibrary };
        var formXml = BuildFormXml(libraries: currentLibraries);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var snapshot = BuildSnapshotUntrackedLibrary([], "msdyn_/Account/AssetCommon.Account.Library.js", ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        // No annotation, no tracked-library orphan — nothing for Flowline to do at all.
        Assert.Empty(plan.Forms);
    }

    [Fact]
    public void Plan_NamespacedExportWithMultiWordLibraryFileName_ResolvesPascalCaseNamespace()
    {
        // Drives ResolveFunction's namespaced branch, so DeriveAutoNamespace/ToPascalCase (mirroring
        // rollup.config.mjs's toPascalCase) actually runs. A multi-word filename catches a broken split.
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "doThing", null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_ns/my_example.js", "exports.doThing = function() {}", "src/my_example.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        var desired = Assert.Single(entry.DesiredHandlers);
        Assert.Equal("MyExample.doThing", desired.FunctionName);
    }

    [Fact]
    public void Plan_DuplicateAnnotationsSameIdentity_DeduplicatedToOneHandler()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved1 = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onLoad() {}", "src/lib1.ts");
        var resolved2 = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onLoad() {}", "src/lib2.ts");

        var snapshot = BuildSnapshot([resolved1, resolved2], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Single(entry.DesiredHandlers);
    }

    // --- flowline:onchange ---

    [Fact]
    public void Plan_OnChangeNoFunctionSingleLowercaseAttribute_DefaultsToOnAttributeChange()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnChange, null, null, "creditlimit");
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onCreditlimitChange() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "onCreditlimitChange");
    }

    [Fact]
    public void Plan_OnChangeNoFunctionPrefixedTwoSegmentAttribute_StripsPrefixAndPascalCasesBothSegments()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnChange, null, null, "new_credit_limit");
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onCreditLimitChange() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "onCreditLimitChange");
    }

    [Fact]
    public void Plan_OnChangeNoFunctionPublisherPrefixedThreeSegmentAttribute_OnlyFirstPrefixStripped()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnChange, null, null, "cr507_risk_rating");
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onRiskRatingChange() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "onRiskRatingChange");
    }

    [Fact]
    public void Plan_OnChangeExplicitFunctionName_PassedThroughUnchangedNotRederived()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnChange, "MyCustomHandler", null, "creditlimit");
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function MyCustomHandler() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "MyCustomHandler");
    }

    [Fact]
    public void Plan_OnChangeExistingHandlerNonMatchingId_UnrecognizedWithOnChangeProposedAnnotation()
    {
        var xdoc = XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new List<FormEventHandler> { new("manualOnChange", "av_/manual.js", Guid.NewGuid(), "") }, "creditlimit");
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        var snapshot = BuildSnapshot([], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal(FormEventType.OnChange, entry.Event);
        Assert.Equal("creditlimit", entry.Attribute);
        var unrecognized = Assert.Single(entry.UnrecognizedHandlers);
        Assert.Equal("// flowline:onchange account \"Account Main\" creditlimit manualOnChange", unrecognized.ProposedAnnotation);
    }

    [Fact]
    public void Plan_OnChangeExistingHandlerOnAttributeWithNoCurrentAnnotation_DetectedAsStaleAndScheduledForRemoval()
    {
        // R13/KTD4: orphan detection for onchange must be reachable via GetOnChangeAttributes alone — no
        // annotation targets "revenue" at all, so the only way this attribute is even considered is by
        // enumerating the XML.
        var staleId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnChange, "onRevenueChange", "av_/lib.js", "revenue");
        var xdoc = XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new List<FormEventHandler> { new("onRevenueChange", "av_/lib.js", staleId, "") }, "revenue");
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        var snapshot = BuildSnapshot([], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal("revenue", entry.Attribute);
        Assert.Empty(entry.DesiredHandlers); // stale, Flowline-owned — dropped automatically
        Assert.Empty(entry.UnrecognizedHandlers);
    }

    [Fact]
    public void Plan_OnChangeGetOnChangeAttributesUnion_CoversBothAnnotationAndXmlAttributesIndependently()
    {
        // "creditlimit" only exists via a current annotation (no XML event yet); "revenue" only exists via
        // an existing XML event (no annotation). Neither alone would surface both — proves the union.
        var staleId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnChange, "onRevenueChange", "av_/lib.js", "revenue");
        var xdoc = XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new List<FormEventHandler> { new("onRevenueChange", "av_/lib.js", staleId, "") }, "revenue");
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnChange, null, null, "creditlimit");
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onCreditlimitChange() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        Assert.Contains(plan.Forms, e => e.Event == FormEventType.OnChange && e.Attribute == "creditlimit"
            && e.DesiredHandlers.Any(h => h.FunctionName == "onCreditlimitChange"));
        Assert.Contains(plan.Forms, e => e.Event == FormEventType.OnChange && e.Attribute == "revenue" && e.DesiredHandlers.Count == 0);
    }

    [Fact]
    public void Plan_OnChangeHandlerOnUntrackedLibraryAlongsideNewAnnotation_ForeignHandlerCarriedThroughUntouched()
    {
        // The untracked handler is the only current entry (no library-need-driven narrow-fallback
        // ambiguity — see KTD4 note above): a brand-new annotation for the same attribute forces a real
        // handlersChanged=true on the (OnChange, "creditlimit") entry itself, so the untracked handler's
        // pass-through behavior is verified on the entry it actually belongs to.
        var xdoc = XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new List<FormEventHandler> { new("untrackedFn", "av_/untracked.js", Guid.NewGuid(), "") }, "creditlimit");
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnChange, null, null, "creditlimit");
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/keep.js", "function onCreditlimitChange() {}", "src/keep.ts");

        var snapshot = BuildSnapshotUntrackedLibrary([resolved], "av_/untracked.js", ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms, e => e.Attribute == "creditlimit");
        Assert.Contains(entry.DesiredHandlers, h => h.FunctionName == "untrackedFn");
        Assert.DoesNotContain(entry.UnrecognizedHandlers, u => u.Handler.FunctionName == "untrackedFn");
    }

    [Fact]
    public void StripPublisherPrefix_NoUnderscore_ReturnsUnchanged()
    {
        Assert.Equal("creditlimit", FormEventPlanner.StripPublisherPrefix("creditlimit"));
    }

    [Fact]
    public void StripPublisherPrefix_SingleUnderscore_StripsPrefix()
    {
        Assert.Equal("creditlimit", FormEventPlanner.StripPublisherPrefix("new_creditlimit"));
    }

    [Fact]
    public void StripPublisherPrefix_MultipleUnderscores_OnlyStripsFirstPrefix()
    {
        Assert.Equal("risk_rating", FormEventPlanner.StripPublisherPrefix("cr507_risk_rating"));
    }

    // --- [bulkEdit] (R6-R8) ---

    [Fact]
    public void Plan_BulkEditOnOnSaveAnnotation_ThrowsNamingOffendingAnnotation()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnSave, null, null, BulkEdit: true);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onSave() {}", "src/bad.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("src/bad.ts", ex.Message);
        Assert.Contains("bulkEdit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_OneOfTwoOnloadAnnotationsHasBulkEdit_UnionEnablesBulkEditOnPlanEntry()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var withBulkEdit = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "First", null, BulkEdit: true), "av_/a.js", "function First() {}", "src/a.ts");
        var withoutBulkEdit = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "Second", null), "av_/b.js", "function Second() {}", "src/b.ts");

        var snapshot = BuildSnapshot([withBulkEdit, withoutBulkEdit], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.True(entry.BulkEditEnabled);
    }

    [Fact]
    public void Plan_PreviouslyBulkEditEnabledFormWithBulkEditAnnotationRemoved_ClearsBulkEditEnabledAndStillEmitsEntry()
    {
        // R8's "previously set, now removed" case, plus the bulk-edit-only-change gate: the handler set
        // itself is unchanged (same function/library/order), so BulkEditEnabled flipping true->false is the
        // ONLY thing different about this push — it must still produce a plan entry, or the executor never
        // gets a chance to clear BehaviorInBulkEditForm.
        var handlerId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var xdoc = XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnLoad,
            [new FormEventHandler("onLoad", "av_/lib.js", handlerId, "")], bulkEditEnabled: true);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "onLoad", null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/lib.js", "function onLoad() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.False(entry.BulkEditEnabled);
    }

    // --- [order:N] and encounter order (R9-R11) ---

    [Fact]
    public void Plan_ThreeUnorderedOnloadAnnotations_ResolveInEncounterOrder()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var first = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "First", null), "av_/a.js", "function First() {}", "src/a.ts");
        var second = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "Second", null), "av_/b.js", "function Second() {}", "src/b.ts");
        var third = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "Third", null), "av_/c.js", "function Third() {}", "src/c.ts");

        var snapshot = BuildSnapshot([first, second, third], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal(["First", "Second", "Third"], entry.DesiredHandlers.Select(h => h.FunctionName));
    }

    [Fact]
    public void Plan_TwoOrderedOnloadAnnotationsAcrossFiles_ResolveAscendingByOrderRegardlessOfEncounterOrder()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        // Encountered in the order [Second, First] (simulating file-scan order), but [order:1] must sort first.
        var orderTwo = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "Second", null, Order: 2), "av_/b.js", "function Second() {}", "src/b.ts");
        var orderOne = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "First", null, Order: 1), "av_/a.js", "function First() {}", "src/a.ts");

        var snapshot = BuildSnapshot([orderTwo, orderOne], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal(["First", "Second"], entry.DesiredHandlers.Select(h => h.FunctionName));
    }

    [Fact]
    public void Plan_MixedOrderedAndUnorderedOnloadAnnotations_OrderedSortFirstThenUnorderedAppendInEncounterOrder()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var unorderedA = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "UnorderedA", null), "av_/a.js", "function UnorderedA() {}", "src/a.ts");
        var orderedOne = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "OrderedOne", null, Order: 1), "av_/b.js", "function OrderedOne() {}", "src/b.ts");
        var unorderedB = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "UnorderedB", null), "av_/c.js", "function UnorderedB() {}", "src/c.ts");

        var snapshot = BuildSnapshot([unorderedA, orderedOne, unorderedB], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal(["OrderedOne", "UnorderedA", "UnorderedB"], entry.DesiredHandlers.Select(h => h.FunctionName));
    }

    [Fact]
    public void Plan_TwoOnloadAnnotationsSameOrderValue_ThrowsNamingBothSourceFiles()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var first = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "First", null, Order: 1), "av_/a.js", "function First() {}", "src/first.ts");
        var second = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "Second", null, Order: 1), "av_/b.js", "function Second() {}", "src/second.ts");

        var snapshot = BuildSnapshot([first, second], ("account", "Account Main", form));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("src/first.ts", ex.Message);
        Assert.Contains("src/second.ts", ex.Message);
        Assert.Contains("order:1", ex.Message);
    }

    [Fact]
    public void Plan_OrderOnlyChangeWithOtherwiseIdenticalHandlerSet_StillEmitsPlanEntry()
    {
        // KTD8: FormEventHandlerDiffer.Diff alone reports (0,0,0) here (same identity/parameters), so this
        // proves the independent orderChanged check is what makes the corrected order actually reach the
        // write path, instead of being silently skipped.
        var idA = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "A", "av_/a.js");
        var idB = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "B", "av_/b.js");
        var current = new List<FormEventHandler> { new("A", "av_/a.js", idA, ""), new("B", "av_/b.js", idB, "") };
        var formXml = BuildFormXml(FormEventType.OnLoad, current.ToHashSet());
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        // Annotations encountered in the OPPOSITE order from the live FormXml (B before A), with no
        // [order:N] — so the newly-computed DesiredHandlers is [B, A], not [A, B].
        var annotationB = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "B", null), "av_/b.js", "function B() {}", "src/b.ts");
        var annotationA = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "A", null), "av_/a.js", "function A() {}", "src/a.ts");

        var snapshot = BuildSnapshot([annotationB, annotationA], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal(["B", "A"], entry.DesiredHandlers.Select(h => h.FunctionName));
    }

    // --- 50-handler cap (R12) ---

    [Fact]
    public void Plan_49ExistingHandlersPlusOneNewAnnotation_SucceedsAt50()
    {
        var current = new HashSet<FormEventHandler>();
        for (var i = 0; i < 49; i++)
        {
            current.Add(new FormEventHandler($"Existing{i}", "av_/existing.js", Guid.NewGuid(), ""));
        }
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        // The 49 existing handlers' library is explicitly untracked (R15 foreign pass-through — a random,
        // non-deterministic id keeps them from being recognized as stale Flowline-owned entries), so they
        // pass through untouched, plus one new Flowline-managed annotation brings the total to exactly 50.
        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "NewOne", null), "av_/new.js", "function NewOne() {}", "src/new.ts");

        var snapshot = BuildSnapshotUntrackedLibrary([annotation], "av_/existing.js", ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal(50, entry.DesiredHandlers.Count);
    }

    [Fact]
    public void Plan_50ExistingHandlersPlusOneNewAnnotation_ThrowsBeforeAnyWrite()
    {
        var current = new HashSet<FormEventHandler>();
        for (var i = 0; i < 50; i++)
        {
            current.Add(new FormEventHandler($"Existing{i}", "av_/existing.js", Guid.NewGuid(), ""));
        }
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "NewOne", null), "av_/new.js", "function NewOne() {}", "src/new.ts");

        var snapshot = BuildSnapshotUntrackedLibrary([annotation], "av_/existing.js", ("account", "Account Main", form));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("50-handler-per-event limit", ex.Message);
    }

    [Fact]
    public void Plan_50CapCountsForeignAndUnrecognizedHandlersNotJustFlowlineManaged()
    {
        // 50 foreign (untracked-library) handlers already on the form, plus one new Flowline-managed
        // annotation for a DIFFERENT library — proves the cap counts the full write-back set, not just
        // the handlers Flowline itself is adding (R12).
        var current = new HashSet<FormEventHandler>();
        for (var i = 0; i < 50; i++)
            current.Add(new FormEventHandler($"Foreign{i}", "thirdparty/lib.js", Guid.NewGuid(), ""));
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "NewOne", null), "av_/new.js", "function NewOne() {}", "src/new.ts");

        var snapshot = BuildSnapshotUntrackedLibrary([annotation], "thirdparty/lib.js", ("account", "Account Main", form));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("50-handler-per-event limit", ex.Message);
    }

    // --- Tab/IFRAME scope enumeration and validation (R5) ---

    [Fact]
    public void Plan_TabStateChangeAnnotationForExistingTab_ProducesEntryScopedToTabName()
    {
        var xdoc = XDocument.Parse("""<form><tabs><tab name="Summary"><columns><column width="100%" /></columns></tab></tabs></form>""");
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.TabStateChange, null, null, "Summary"),
            "av_/lib.js", "function onSummaryTabStateChange() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([annotation], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms, e => e.Event == FormEventType.TabStateChange);
        Assert.Equal("Summary", entry.Attribute);
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "onSummaryTabStateChange");
    }

    [Fact]
    public void Plan_TabStateChangeAnnotationForNonexistentTab_ThrowsCleanlyNamingTabAndSourceFile()
    {
        // Correctness-review regression: an unresolvable Tab/IFRAME scope must fail cleanly at plan time
        // (FlowlineException, before any write), not surface as an unhandled InvalidOperationException
        // from deep inside the executor and abort the entire push.
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.TabStateChange, null, null, "DoesNotExist"),
            "av_/lib.js", "function onX() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([annotation], ("account", "Account Main", form));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("DoesNotExist", ex.Message);
        Assert.Contains("src/lib.ts", ex.Message);
    }

    [Fact]
    public void Plan_OnReadyStateCompleteAnnotationForNonexistentControl_ThrowsCleanlyNamingControlAndSourceFile()
    {
        var formXml = BuildFormXml();
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnReadyStateComplete, null, null, "IFRAME_doesNotExist"),
            "av_/lib.js", "function onX() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([annotation], ("account", "Account Main", form));

        var ex = Assert.Throws<FlowlineException>(() => _planner.Plan(snapshot));

        Assert.Contains("IFRAME_doesNotExist", ex.Message);
        Assert.Contains("src/lib.ts", ex.Message);
    }

    [Fact]
    public void Plan_OnReadyStateCompleteAnnotationWritesPrefixedControlId_ResolvesToLiveControlAsSingleScope()
    {
        // Regression: an annotation-supplied "IFRAME_"-prefixed control id must collapse onto the same
        // scope key GetIframeControlIdsWithReadyStateHandlers' live scan reports (bare, prefix-stripped) —
        // otherwise the same physical control plans as two distinct scopes and its <events> element gets
        // written twice, the second write clobbering the first.
        var xdoc = XDocument.Parse(
            """<form><tabs><tab name="Details"><columns><column width="100%"><sections><section name="s1"><rows><row><cell id="cell1"><control id="IFRAME_myFrame" classid="{FD2A7985-3187-444E-908D-6624B21F69C0}"><parameters><Url>https://example.com</Url></parameters></control><events><event name="onreadystatecomplete" application="false" active="false"><Handlers><Handler functionName="Old.onReady" libraryName="av_/lib.js" handlerUniqueId="{11111111-1111-1111-1111-111111111111}" enabled="true" parameters="" passExecutionContext="true" /></Handlers></event></events></cell></row></rows></section></sections></column></columns></tab></tabs></form>""");
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", xdoc.ToString());

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnReadyStateComplete, null, null, "IFRAME_myFrame"),
            "av_/lib.js", "function onMyFrameReadyStateComplete() {}", "src/lib.ts");

        var snapshot = BuildSnapshot([annotation], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms, e => e.Event == FormEventType.OnReadyStateComplete);
        Assert.Equal("myFrame", entry.Attribute);
        Assert.Single(entry.DesiredHandlers, h => h.FunctionName == "onMyFrameReadyStateComplete");
    }

    // --- Interleave-preserving merge (product decision: OOB/foreign handlers outrank Flowline ordering) ---

    [Fact]
    public void Plan_ForeignHandlerPositionedBeforeFlowlineHandlerLive_KeepsForeignHandlerFirstWhenPlanChanges()
    {
        // Product decision: a Microsoft OOB or third-party script that already runs BEFORE a
        // Flowline-managed handler in the live Form Event Pipeline must never be silently relocated to
        // run after it — content is preserved for foreign handlers (R15) AND their relative execution
        // position is too, even when something else about the push forces a plan entry to emit (here: a
        // brand-new second Flowline annotation, so the reordering behavior is actually observable).
        var flowlineId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var foreignHandler = new FormEventHandler("msdyn.OobHandler", "msdyn_lib.js", Guid.NewGuid(), "");
        // Live document order: foreign handler FIRST, Flowline handler SECOND.
        var current = new List<FormEventHandler> { foreignHandler, new("onLoad", "av_/lib.js", flowlineId, "") };
        var formXml = BuildFormXml(FormEventType.OnLoad, current.ToHashSet());
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var existingAnnotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "onLoad", null), "av_/lib.js", "function onLoad() {}", "src/lib.ts");
        var newAnnotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "NewHandler", null), "av_/new.js", "function NewHandler() {}", "src/new.ts");

        var snapshot = BuildSnapshotUntrackedLibrary([existingAnnotation, newAnnotation], "msdyn_lib.js", ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        // Foreign handler keeps its original slot 0; the existing Flowline handler keeps its slot 1;
        // the brand-new handler (no prior slot) appends at the end.
        Assert.Equal(["msdyn.OobHandler", "onLoad", "NewHandler"], entry.DesiredHandlers.Select(h => h.FunctionName));
    }

    [Fact]
    public void Plan_NewFlowlineHandlerAddedAlongsideExistingForeignHandler_ForeignHandlerKeepsItsSlotNewHandlerFillsGapOrAppends()
    {
        var foreignHandler = new FormEventHandler("msdyn.OobHandler", "msdyn_lib.js", Guid.NewGuid(), "");
        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { foreignHandler });
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        // Brand-new Flowline annotation — no prior slot exists for it, so it has no original position to
        // preserve and simply appends after the existing foreign handler.
        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, "NewHandler", null), "av_/new.js", "function NewHandler() {}", "src/new.ts");

        var snapshot = BuildSnapshotUntrackedLibrary([annotation], "msdyn_lib.js", ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Equal(["msdyn.OobHandler", "NewHandler"], entry.DesiredHandlers.Select(h => h.FunctionName));
    }
}
