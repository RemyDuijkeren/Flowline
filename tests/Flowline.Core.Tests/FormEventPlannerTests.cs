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

    static FormEventSnapshot BuildSnapshot(
        IReadOnlyList<ResolvedFormEventAnnotation> annotations,
        params (string Entity, string Form, DataverseForm Form2)[] forms)
    {
        var dict = forms.ToDictionary(f => (f.Entity, f.Form), f => f.Form2);
        return new FormEventSnapshot(annotations, dict);
    }

    [Fact]
    public void Plan_DesiredMatchesCurrentExactlyNoUnrecognized_ProducesNoPlanEntry()
    {
        var handlerId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/lib.js");
        var current = new HashSet<FormHandler> { new("onLoad", "av_/lib.js", handlerId, "") };
        var libraries = new HashSet<FormLibraryEntry> { new("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js")) };
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
        var current = new HashSet<FormHandler>
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
        var current = new HashSet<FormHandler>
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
        Assert.Contains(entry.UnrecognizedHandlers, h => h.FunctionName == "manualFn");
    }

    [Fact]
    public void Plan_HandlerOnUnreferencedLibraryWithNonMatchingId_IsKeptAndFlaggedUnrecognized()
    {
        // Same treatment as a stale handler on a still-referenced library: unrecognized status is
        // evaluated per-handler, independent of whether its library is tracked by this push at all.
        var keptId = FormEventDeterministicId.ForHandler("account", "Account Main", FormEventType.OnLoad, "onLoad", "av_/keep.js");
        var current = new HashSet<FormHandler>
        {
            new("onLoad", "av_/keep.js", keptId, ""),
            new("untrackedFn", "av_/untracked.js", Guid.NewGuid(), "")
        };
        var formXml = BuildFormXml(FormEventType.OnLoad, current);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/keep.js", "function onLoad() {}", "src/keep.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Contains(entry.DesiredHandlers, h => h.FunctionName == "untrackedFn" && h.LibraryName == "av_/untracked.js");
        Assert.Contains(entry.UnrecognizedHandlers, h => h.FunctionName == "untrackedFn");
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
        var badResolved1 = new ResolvedFormEventAnnotation(badAnnotation1, "av_/bad1.js", "// nothing here", "src/bad1.ts");

        var formXmlBad2 = BuildFormXml();
        var formBad2 = new DataverseForm(Guid.NewGuid(), "Lead Main", "lead", formXmlBad2);
        var badAnnotation2 = new FormEventAnnotation("lead", "Lead Main", FormEventType.OnLoad, "missingFn2", null);
        var badResolved2 = new ResolvedFormEventAnnotation(badAnnotation2, "av_/bad2.js", "// nothing here either", "src/bad2.ts");

        var snapshot = BuildSnapshot(
            [goodResolved, badResolved1, badResolved2],
            ("account", "Account Main", formGood),
            ("contact", "Contact Main", formBad1),
            ("lead", "Lead Main", formBad2));

        var ex = Assert.Throws<InvalidOperationException>(() => _planner.Plan(snapshot));

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
        var current = new HashSet<FormHandler> { new("onLoad", "av_/lib.js", handlerId, "oldParams") };
        var libraries = new HashSet<FormLibraryEntry> { new("av_/lib.js", FormEventDeterministicId.ForLibrary("av_/lib.js")) };
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
    public void Plan_UnreferencedLibraryStaysInDesiredLibraries_AddOnlyNeverRemoved()
    {
        var orphanLibrary = new FormLibraryEntry("av_/orphan.js", FormEventDeterministicId.ForLibrary("av_/orphan.js"));
        var currentLibraries = new HashSet<FormLibraryEntry> { orphanLibrary };
        var formXml = BuildFormXml(libraries: currentLibraries);
        var form = new DataverseForm(Guid.NewGuid(), "Account Main", "account", formXml);

        var annotation = new FormEventAnnotation("account", "Account Main", FormEventType.OnLoad, null, null);
        var resolved = new ResolvedFormEventAnnotation(annotation, "av_/newlib.js", "function onLoad() {}", "src/newlib.ts");

        var snapshot = BuildSnapshot([resolved], ("account", "Account Main", form));

        var plan = _planner.Plan(snapshot);

        var entry = Assert.Single(plan.Forms);
        Assert.Contains(entry.DesiredLibraries, l => l.Name == "av_/orphan.js");
        Assert.Contains(entry.DesiredLibraries, l => l.Name == "av_/newlib.js");
        Assert.Equal(2, entry.DesiredLibraries.Count);
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
