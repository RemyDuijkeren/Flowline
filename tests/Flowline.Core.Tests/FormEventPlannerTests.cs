using System.Xml.Linq;
using Flowline.Core.Models;
using Flowline.Core.Services;
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
        var currentOnSave = new HashSet<FormEventHandler> { new("onSave", "av_/shared.js", Guid.NewGuid(), "") }; // kept via annotation below

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
}
