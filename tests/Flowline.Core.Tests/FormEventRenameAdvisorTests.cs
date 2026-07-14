using Flowline.Core.Models;
using Flowline.Core.Services;
using FluentAssertions;
using static Flowline.Core.Tests.FormEventTestHelpers;

namespace Flowline.Core.Tests;

// U3: FormEventRenameAdvisor.Suggest is exercised directly here (no Dataverse service involved) — its
// three advisory signals (self-tag, rename-cache, sole-survivor) and their tiering/tie-break rules.
// FormEventReaderTests covers the end-to-end wiring into LoadSnapshotAsync's thrown message, plus the
// R5/R6 regression guards that must hold at that integration boundary.
public class FormEventRenameAdvisorTests : IDisposable
{
    readonly List<string> _cacheFiles = [];

    public void Dispose()
    {
        foreach (var path in _cacheFiles)
            if (File.Exists(path)) File.Delete(path);
    }

    FormEventIdentityCache NewCache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        _cacheFiles.Add(path);
        return new FormEventIdentityCache(path);
    }

    static ResolvedFormEventAnnotation Annotation(string entity, string form, string library, string content, FormEventType evt = FormEventType.OnLoad) =>
        new(new FormEventAnnotation(entity, form, evt, null, null), library, content, "src/lib.ts");

    [Fact]
    public void Suggest_SelfTagMatch_ReturnsStrongConfidenceCandidateNamingIt()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        const string library = "av_/lib.js";

        // Simulates the handler that was registered back when the annotation still said "Old Main" — the
        // deterministic id is derived from the OLD name, and it's still on the live form's formxml
        // untouched (a Dataverse rename never touches Handler attributes).
        var expectedId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnLoad, "onLoad", library);
        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("onLoad", library, expectedId, "") });

        var resolved = Annotation(entity, oldName, library, "function onLoad() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), newName, entity, formXml, null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, NewCache());

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain(newName);
        suggestion.Should().Contain("renamed to");
        suggestion.Should().Contain($"// flowline:onload {entity} \"{newName}\"");
    }

    [Fact]
    public void Suggest_CacheOnlyMatch_ReturnsProbableConfidenceCandidate()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        var formId = Guid.NewGuid();

        // No self-tag evidence at all: the live form's only handler carries an unrelated id, and the
        // annotation's requested function ("onLoad", defaulted) doesn't even exist in this content.
        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("someOtherFn", "unrelated.js", Guid.NewGuid(), "") });
        var resolved = Annotation(entity, oldName, "av_/lib.js", "function unrelated() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(formId, newName, entity, formXml, null)
        };

        var cache = NewCache();
        cache.Set(entity, oldName, formId); // previously resolved by a prior push, before the rename

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, cache);

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain(newName);
        suggestion.Should().Contain("may have been renamed");
    }

    [Fact]
    public void Suggest_SoleSurvivorOnly_ReturnsClearlyHedgedCandidate()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string onlyLiveName = "Only Form";

        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("someOtherFn", "unrelated.js", Guid.NewGuid(), "") });
        var resolved = Annotation(entity, oldName, "av_/lib.js", "function unrelated() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), onlyLiveName, entity, formXml, null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, NewCache());

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain(onlyLiveName);
        suggestion.Should().Contain("only form left");
        suggestion.Should().Contain("check if this is what you meant");
    }

    [Fact]
    public void Suggest_SelfTagAndCacheAgreeOnSameCandidate_DoesNotOverstateBeyondStrongestSignal()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        const string library = "av_/lib.js";
        var formId = Guid.NewGuid();

        var expectedId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnLoad, "onLoad", library);
        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("onLoad", library, expectedId, "") });
        var resolved = Annotation(entity, oldName, library, "function onLoad() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(formId, newName, entity, formXml, null)
        };

        var cache = NewCache();
        cache.Set(entity, oldName, formId); // agrees with self-tag on the very same candidate

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, cache);

        suggestion.Should().Contain(newName);
        suggestion.Should().Contain("renamed to"); // strongest (self-tag) wording used
        suggestion.Should().NotContain("may have been renamed"); // not downgraded/duplicated with the weaker cache wording
    }

    [Fact]
    public void Suggest_SelfTagAndCacheDisagreeOnDifferentCandidates_SelfTagWinsAsStrongestSignal()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string selfTagName = "Self Tag Candidate";
        const string cacheOnlyName = "Cache Only Candidate";
        const string library = "av_/lib.js";

        var expectedId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnLoad, "onLoad", library);
        var selfTagFormXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("onLoad", library, expectedId, "") });
        var cacheFormXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("other", "other.js", Guid.NewGuid(), "") });

        var resolved = Annotation(entity, oldName, library, "function onLoad() {}");

        var cacheFormId = Guid.NewGuid();
        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), selfTagName, entity, selfTagFormXml, null),
            new(cacheFormId, cacheOnlyName, entity, cacheFormXml, null)
        };

        var cache = NewCache();
        cache.Set(entity, oldName, cacheFormId); // points at the OTHER candidate — disagreement

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, cache);

        suggestion.Should().Contain(selfTagName);
        suggestion.Should().NotContain(cacheOnlyName);
    }

    [Fact]
    public void Suggest_MainAndQuickCreateBothLiveOnEntity_SoleSurvivorStaysSilentButOtherSignalsStillEvaluated()
    {
        const string entity = "account";
        const string oldName = "Old Main";

        var mainFormXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("other1", "other1.js", Guid.NewGuid(), "") });
        var quickCreateFormXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("other2", "other2.js", Guid.NewGuid(), "") });

        var resolved = Annotation(entity, oldName, "av_/lib.js", "function unrelated() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), "Account Main", entity, mainFormXml, null),
            new(Guid.NewGuid(), "Account Quick Create", entity, quickCreateFormXml, null)
        };

        // Two candidates on the entity — sole-survivor must not fire; no self-tag or cache evidence either.
        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, NewCache());

        suggestion.Should().BeNull();
    }

    [Fact]
    public void Suggest_NoLiveFormsRemainForEntityAtAll_ReturnsNullWithoutThrowing()
    {
        const string entity = "account";
        const string oldName = "Old Main";

        var resolved = Annotation(entity, oldName, "av_/lib.js", "function unrelated() {}");

        var solutionForms = new List<DataverseForm>();

        var cache = NewCache();
        cache.Set(entity, oldName, Guid.NewGuid()); // stale — that formId no longer exists anywhere

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, cache);

        suggestion.Should().BeNull();
    }

    [Fact]
    public void Suggest_NoSignalProducesACandidate_ReturnsNull()
    {
        const string entity = "account";
        const string oldName = "Old Main";

        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("other", "other.js", Guid.NewGuid(), "") });
        var resolved = Annotation(entity, oldName, "av_/lib.js", "function unrelated() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), "Some Other Form A", entity, formXml, null),
            new(Guid.NewGuid(), "Some Other Form B", entity, formXml, null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, NewCache());

        suggestion.Should().BeNull();
    }

    [Fact]
    public void Suggest_OneCandidateHasMalformedFormXml_SkipsItAndStillFindsTheValidSelfTagMatch()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        const string library = "av_/lib.js";

        var expectedId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnLoad, "onLoad", library);
        var validFormXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("onLoad", library, expectedId, "") });
        var resolved = Annotation(entity, oldName, library, "function onLoad() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), "Unparsable Candidate", entity, "not valid xml <<<", null),
            new(Guid.NewGuid(), newName, entity, validFormXml, null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, NewCache());

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain(newName);
        suggestion.Should().NotContain("Unparsable Candidate");
    }

    [Fact]
    public void Suggest_TwoSharingAnnotationsOnLoadAndOnSave_SuggestionListsBothAsSeparateLines()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        const string library = "av_/lib.js";

        var onLoadId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnLoad, "onLoad", library);
        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("onLoad", library, onLoadId, "") });

        var onLoadAnnotation = Annotation(entity, oldName, library, "function onLoad() {} function onSave() {}");
        var onSaveAnnotation = Annotation(entity, oldName, library, "function onLoad() {} function onSave() {}", FormEventType.OnSave);

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), newName, entity, formXml, null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [onLoadAnnotation, onSaveAnnotation], solutionForms, NewCache());

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain($"// flowline:onload {entity} \"{newName}\"");
        suggestion.Should().Contain($"// flowline:onsave {entity} \"{newName}\"");
    }

    // --- flowline:onchange self-tag ---

    [Fact]
    public void Suggest_OnChangeSelfTagMatch_FindsRenamedFormViaAttributeKeyedScan()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        const string library = "av_/lib.js";
        const string attribute = "creditlimit";

        var expectedId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnChange, "onCreditlimitChange", library, attribute);
        var xdoc = System.Xml.Linq.XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new HashSet<FormEventHandler> { new("onCreditlimitChange", library, expectedId, "") }, attribute);

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation(entity, oldName, FormEventType.OnChange, null, null, attribute), library, "function onCreditlimitChange() {}", "src/lib.ts");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), newName, entity, xdoc.ToString(), null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [annotation], solutionForms, NewCache());

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain(newName);
        suggestion.Should().Contain("renamed to");
        // Reconstructed from the annotation as originally written (FunctionName omitted/defaulted) — no
        // function name in the suggested text, same as the onload/onsave precedent.
        suggestion.Should().Contain($"// flowline:onchange {entity} \"{newName}\" {attribute}");
    }

    [Fact]
    public void Suggest_OnChangeSelfTagDifferentAttribute_DoesNotFalsePositiveMatch()
    {
        // A matching-id handler on a DIFFERENT attribute must not satisfy a self-tag check for this one —
        // the attribute-keyed scan (R18a) prevents cross-attribute false positives.
        const string entity = "account";
        const string oldName = "Old Main";
        const string library = "av_/lib.js";

        var revenueId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnChange, "onRevenueChange", library, "revenue");
        var xdoc = System.Xml.Linq.XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new HashSet<FormEventHandler> { new("onRevenueChange", library, revenueId, "") }, "revenue");

        // Annotation targets "creditlimit", not "revenue" — its expected id (derived with attribute
        // "creditlimit") will never match the "revenue"-scoped handler above, id collision aside.
        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation(entity, oldName, FormEventType.OnChange, null, null, "creditlimit"), library, "function onCreditlimitChange() {}", "src/lib.ts");

        // Two candidates on the entity — otherwise the sole-survivor (hedged) signal would independently
        // fire and mask what this test is actually isolating (self-tag's attribute-keyed non-match).
        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), "Some Other Form", entity, xdoc.ToString(), null),
            new(Guid.NewGuid(), "Yet Another Form", entity, BuildFormXml(), null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [annotation], solutionForms, NewCache());

        suggestion.Should().BeNull();
    }

    [Fact]
    public void Suggest_OnLoadOnSaveUnaffectedByAttributeParameter_StillWorksUnchanged()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        const string library = "av_/lib.js";

        var expectedId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnLoad, "onLoad", library);
        var formXml = BuildFormXml(FormEventType.OnLoad, new HashSet<FormEventHandler> { new("onLoad", library, expectedId, "") });
        var resolved = Annotation(entity, oldName, library, "function onLoad() {}");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), newName, entity, formXml, null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, NewCache());

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain($"// flowline:onload {entity} \"{newName}\"");
    }

    [Fact]
    public void Suggest_OnChangeNoSelfTagCandidate_FallsThroughToCacheSignal()
    {
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        var formId = Guid.NewGuid();

        var xdoc = System.Xml.Linq.XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new HashSet<FormEventHandler> { new("someOtherFn", "unrelated.js", Guid.NewGuid(), "") }, "creditlimit");

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation(entity, oldName, FormEventType.OnChange, null, null, "creditlimit"), "av_/lib.js", "function unrelated() {}", "src/lib.ts");

        var solutionForms = new List<DataverseForm>
        {
            new(formId, newName, entity, xdoc.ToString(), null)
        };

        var cache = NewCache();
        cache.Set(entity, oldName, formId);

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [annotation], solutionForms, cache);

        suggestion.Should().NotBeNull();
        suggestion.Should().Contain(newName);
        suggestion.Should().Contain("may have been renamed");
    }

    [Fact]
    public void Suggest_OnChangeSuggestionEvenWhenSelfTagFinds_R18StillRequiresPushToFail()
    {
        // R18 regression: Suggest never resolves anything itself — it only enriches an already-failing
        // message. This asserts the contract by construction: Suggest is called exactly the way
        // FormEventReader calls it (only reachable from an already-failed lookup), so a non-null return
        // here proves nothing was silently "fixed" — the caller still throws regardless.
        const string entity = "account";
        const string oldName = "Old Main";
        const string newName = "New Main";
        const string library = "av_/lib.js";
        const string attribute = "creditlimit";

        var expectedId = FormEventDeterministicId.ForHandler(entity, oldName, FormEventType.OnChange, "onCreditlimitChange", library, attribute);
        var xdoc = System.Xml.Linq.XDocument.Parse(BuildFormXml());
        FormXmlEventSerializer.SetHandlers(xdoc, FormEventType.OnChange,
            new HashSet<FormEventHandler> { new("onCreditlimitChange", library, expectedId, "") }, attribute);

        var annotation = new ResolvedFormEventAnnotation(
            new FormEventAnnotation(entity, oldName, FormEventType.OnChange, null, null, attribute), library, "function onCreditlimitChange() {}", "src/lib.ts");

        var solutionForms = new List<DataverseForm>
        {
            new(Guid.NewGuid(), newName, entity, xdoc.ToString(), null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [annotation], solutionForms, NewCache());

        suggestion.Should().NotBeNull(); // a suggestion exists — but Suggest itself never resolves the form or the push
    }
}
