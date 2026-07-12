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

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>
        {
            (Guid.NewGuid(), newName, entity, formXml, null)
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

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>
        {
            (formId, newName, entity, formXml, null)
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

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>
        {
            (Guid.NewGuid(), onlyLiveName, entity, formXml, null)
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

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>
        {
            (formId, newName, entity, formXml, null)
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
        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>
        {
            (Guid.NewGuid(), selfTagName, entity, selfTagFormXml, null),
            (cacheFormId, cacheOnlyName, entity, cacheFormXml, null)
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

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>
        {
            (Guid.NewGuid(), "Account Main", entity, mainFormXml, null),
            (Guid.NewGuid(), "Account Quick Create", entity, quickCreateFormXml, null)
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

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>();

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

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>
        {
            (Guid.NewGuid(), "Some Other Form A", entity, formXml, null),
            (Guid.NewGuid(), "Some Other Form B", entity, formXml, null)
        };

        var suggestion = FormEventRenameAdvisor.Suggest(entity, oldName, [resolved], solutionForms, NewCache());

        suggestion.Should().BeNull();
    }
}
