using System.ServiceModel;
using System.Text;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class FormEventReader(IAnsiConsole console)
{
    // systemform.type raw optionset values: Main = 2, Quick Create Form = 7.
    static readonly object[] SupportedFormTypes = [2, 7];

    // solutioncomponent.componenttype for System Form — mirrors OrphanCleanupService's
    // NameResolvableTypes[60] = ("systemform", "formid", "name").
    const int SystemFormComponentType = 60;

    // Mirrors GenerateReader/OrphanCleanupService's cap for concurrent Dataverse metadata/query fan-out.
    const int MaxConcurrentRequests = 20;

    // suppressWarnings: KTD12 runs this reader twice per push (cleanup pass, then registration pass) —
    // both re-scan the same local JS files and would otherwise print the exact same warning twice. Only
    // the registration (second, fuller) pass shows these, matching the existing "up to date"/dry-run-preview
    // dedup convention in FormEventService.SyncAsync.
    public async Task<FormEventSnapshot> LoadSnapshotAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        string? formEventCachePath = null,
        bool suppressWarnings = false,
        CancellationToken cancellationToken = default)
    {
        // formEventCachePath is caller-resolved (Flowline.Core has no reference to the Flowline CLI
        // project's FlowlineStoragePaths — see FormEventIdentityCache's own doc comment). No path means
        // no cache: skip it entirely rather than writing to a disposable random file, which would leave a
        // stray temp JSON per call for no benefit (nothing ever reads it back).
        var cache = formEventCachePath is null ? null : new FormEventIdentityCache(formEventCachePath);

        // Deliberate second call to the same reader WebResourceService already uses elsewhere in the
        // same push — cheap, and keeps this reader independent of a shared snapshot being threaded in.
        var webResourceReader = new WebResourceReader(console);
        var webResourceSnapshot = await webResourceReader
            .LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken)
            .ConfigureAwait(false);

        // R15: every JS web resource tracked by this project, not just files that currently carry an
        // annotation — this is what R15 enforcement (U5) filters Handler.libraryName ownership against.
        var trackedLibraryNames = webResourceSnapshot.LocalResources.Values
            .Where(r => r.Type == WebResourceType.Js)
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolvedAnnotations = CollectAnnotations(webResourceSnapshot.LocalResources.Values, suppressWarnings);

        // Independent per-entity/per-form Dataverse lookups — run concurrently rather than one round-trip
        // at a time, bounded so a project with many entities/forms doesn't fan out unbounded requests.
        using var gate = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);

        // Forward direction (KTD3): logical name -> ObjectTypeCode, for entities referenced by annotations.
        var entities = resolvedAnnotations.Select(a => a.Annotation.Entity).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var objectTypeCodeResults = await Task.WhenAll(entities.Select(async entity =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return (Entity: entity, Code: await ResolveObjectTypeCodeAsync(service, entity, cancellationToken).ConfigureAwait(false)); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        var objectTypeCodes = objectTypeCodeResults.ToDictionary(r => r.Entity, r => r.Code, StringComparer.OrdinalIgnoreCase);

        // R14: every systemform that's a component of this solution, not just forms named by a current
        // annotation. Filters directly on solutioncomponent.solutionid using the id WebResourceReader
        // already resolved above — no second join to solution by uniquename needed (mirrors
        // WebResourceReader.GetWebResourcesForSolutionAsync's single-link shape).
        var solutionFormEntities = await QuerySolutionScopedFormsAsync(service, webResourceSnapshot.Solution.Id, cancellationToken).ConfigureAwait(false);

        // Confirmed live: systemform.objecttypecode, read back from a result row, is the entity's LOGICAL
        // NAME as a string (e.g. "contact") — not a numeric ObjectTypeCode at all. This contradicts KTD3's
        // finding, which is about the numeric type QueryExpression FILTER values need for this attribute
        // (still true, still used by ResolveObjectTypeCodeAsync's forward-direction lookup above), not the
        // CLR type/value actually returned when reading a row. Dataverse's EntityName attribute type
        // auto-resolves to the logical name for API consumers — no reverse ObjectTypeCode -> logical-name
        // lookup needed at all.
        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)>();
        foreach (var formEntity in solutionFormEntities)
        {
            var entityLogicalName = formEntity.GetAttributeValue<string>("objecttypecode");
            if (string.IsNullOrEmpty(entityLogicalName))
            {
                if (!suppressWarnings)
                    console.Warning($"systemform '{formEntity.GetAttributeValue<string>("name")}' ({formEntity.Id}) has no objecttypecode — skipped from form-event resolution.");
                continue;
            }

            // RowVersion is a first-class SDK property (Dataverse's optimistic-concurrency token, aka
            // @odata.etag) always populated on retrieve regardless of ColumnSet - no explicit column
            // selection needed.
            solutionForms.Add((
                formEntity.Id,
                formEntity.GetAttributeValue<string>("name"),
                entityLogicalName,
                formEntity.GetAttributeValue<string>("formxml"),
                formEntity.RowVersion));
        }

        var resolvedForms = new Dictionary<(string Entity, string Form), DataverseForm>(FormKeyComparer.Instance);
        var ambiguousCounts = new Dictionary<(string Entity, string Form), int>(FormKeyComparer.Instance);
        // U2: every successful resolution recorded, not just ones a current annotation references — gives
        // a later rename-detection unit (U3) data for a form that was renamed away from too. Batched into
        // one cache write below rather than one per form, so a large solution's cache file isn't
        // read-modified-written once per resolved form.
        var cacheResolutions = new List<(string Entity, string Name, Guid FormId)>();

        foreach (var group in solutionForms.GroupBy(f => (f.EntityLogicalName, f.Name), FormKeyComparer.Instance))
        {
            var matches = group.ToList();
            if (matches.Count == 1)
            {
                resolvedForms[group.Key] = new DataverseForm(matches[0].Id, matches[0].Name, matches[0].EntityLogicalName, matches[0].FormXml, matches[0].RowVersion);
                cacheResolutions.Add((matches[0].EntityLogicalName, matches[0].Name, matches[0].Id));
            }
            else
                // Ambiguous within this solution — can't be represented by the unique (Entity, Form) key.
                // Only surfaced as an error below when an annotation actually targets it; an ambiguous
                // orphan form with no annotation is simply absent from Forms (rare edge case).
                ambiguousCounts[group.Key] = matches.Count;
        }

        cache?.SetMany(cacheResolutions);

        var formErrors = new Dictionary<(string Entity, string Form), string>(FormKeyComparer.Instance);

        var pairs = resolvedAnnotations
            .Select(a => (a.Annotation.Entity, a.Annotation.Form))
            .Distinct()
            .ToList();

        var pairResults = await Task.WhenAll(pairs.Select(async pair =>
        {
            var (entity, form) = pair;

            // Entity failed to resolve — skip the form lookup, the per-annotation error is reported below.
            if (objectTypeCodes[entity] is not { } objectTypeCode)
                return (Pair: pair, Error: (string?)null);

            if (resolvedForms.ContainsKey(pair))
                return (Pair: pair, Error: (string?)null);

            if (ambiguousCounts.TryGetValue(pair, out var count))
                return (Pair: pair, Error: $"form '{form}' for entity '{entity}' is ambiguous — {count} systemform records matched.");

            // Not found within the solution-scoped set — fall back to an unscoped lookup so R8 ("doesn't
            // exist at all") and R8a ("exists, but isn't a component of this solution") get distinct messages.
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            List<Entity> globalMatches;
            try { globalMatches = await QueryFormsAsync(service, objectTypeCode, form, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }

            return globalMatches.Count switch
            {
                0 => (Pair: pair, Error: BuildFormNotFoundMessage(entity, form, resolvedAnnotations, solutionForms, cache)),
                _ => (Pair: pair, Error: $"form '{form}' for entity '{entity}' exists in Dataverse but is not a component of solution '{solutionName}'.")
            };
        })).ConfigureAwait(false);

        foreach (var (pair, error) in pairResults)
            if (error is not null) formErrors[pair] = error;

        var errors = new List<string>();
        var validAnnotations = new List<ResolvedFormEventAnnotation>();

        foreach (var resolved in resolvedAnnotations)
        {
            var entity = resolved.Annotation.Entity;
            var form = resolved.Annotation.Form;

            if (objectTypeCodes[entity] is null)
            {
                errors.Add($"{resolved.SourceFile}: entity '{entity}' not found in Dataverse.");
                continue;
            }

            if (formErrors.TryGetValue((entity, form), out var formError))
            {
                errors.Add($"{resolved.SourceFile}: {formError}");
                continue;
            }

            validAnnotations.Add(resolved);
        }

        // Failures are isolated per-annotation above — every annotation gets a chance to resolve before
        // this throws, so a single bad declaration never prevents the others from being attempted.
        // FlowlineException, not a raw exception: an unresolvable entity/form is a user-actionable
        // annotation problem, not an internal bug — Program.cs prints it as a clean "Error: ..." line
        // instead of dumping a full stack trace for something the user needs to fix.
        if (errors.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Form event annotations failed to resolve:\n" + string.Join("\n", errors));

        return new FormEventSnapshot(validAnnotations.AsReadOnly(), trackedLibraryNames, resolvedForms.AsReadOnly());
    }

    // U3: R6 — on a name-lookup miss, ask FormEventRenameAdvisor for an evidence-gated suggestion and
    // append it (never replace, never resolve) to today's plain "not found" message. When the advisor
    // finds nothing, this returns byte-for-byte the original message (R6's negative case).
    static string BuildFormNotFoundMessage(
        string entity, string form,
        List<ResolvedFormEventAnnotation> resolvedAnnotations,
        List<(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion)> solutionForms,
        FormEventIdentityCache? cache)
    {
        var baseMessage = $"form '{form}' not found for entity '{entity}' (Main or Quick Create form).";

        var sharingAnnotations = resolvedAnnotations
            .Where(a => FormKeyComparer.Instance.Equals((a.Annotation.Entity, a.Annotation.Form), (entity, form)))
            .ToList();

        // Retyped to the named DataverseForm record at this cross-class boundary (rather than passing the
        // raw positional tuple further) so Name/EntityLogicalName can't get silently transposed by a future edit.
        var candidateForms = solutionForms
            .Select(f => new DataverseForm(f.Id, f.Name, f.EntityLogicalName, f.FormXml, f.RowVersion))
            .ToList();

        var suggestion = FormEventRenameAdvisor.Suggest(entity, form, sharingAnnotations, candidateForms, cache);
        return suggestion is null ? baseMessage : baseMessage + suggestion;
    }

    List<ResolvedFormEventAnnotation> CollectAnnotations(IEnumerable<LocalWebResource> localResources, bool suppressWarnings)
    {
        var result = new List<ResolvedFormEventAnnotation>();
        foreach (var resource in localResources)
        {
            if (resource.Type != WebResourceType.Js) continue;

            // Content is already loaded in memory (base64) — decode once and scan that instead of a
            // second disk read via the file-path overload.
            var content = string.IsNullOrEmpty(resource.Content)
                ? string.Empty
                : Encoding.UTF8.GetString(Convert.FromBase64String(resource.Content));

            var parsed = FormEventAnnotationParser.ParseAnnotations(content.Split('\n'));

            // A line that clearly intends to be a flowline:on... annotation but fails the strict grammar
            // (e.g. a form name missing entirely, or a multi-word form name with no quotes / mismatched
            // quotes — R3 accepts single or double, but not unquoted or mixed) would otherwise register
            // nothing with no indication why. Surfaced here rather than an ordinary "not a match at all"
            // comment, which stays silently ignored.
            if (!suppressWarnings)
                foreach (var line in parsed.MalformedLines)
                    console.Warning($"{Markup.Escape(resource.RelativePath)}: malformed flowline annotation, ignored — form names with spaces must be wrapped in matching double or single quotes: {Markup.Escape(line)}");

            if (parsed.Annotations.Count == 0) continue;

            foreach (var annotation in parsed.Annotations)
                result.Add(new ResolvedFormEventAnnotation(annotation, resource.Name, content, resource.RelativePath));
        }
        return result;
    }

    static async Task<int?> ResolveObjectTypeCodeAsync(
        IOrganizationServiceAsync2 service, string entityLogicalName, CancellationToken cancellationToken)
    {
        var request = new RetrieveEntityRequest
        {
            LogicalName = entityLogicalName,
            EntityFilters = EntityFilters.Entity,
            RetrieveAsIfPublished = false
        };
        try
        {
            var response = (RetrieveEntityResponse)await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return response.EntityMetadata?.ObjectTypeCode;
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            // A genuinely nonexistent entity logical name faults rather than returning null metadata —
            // same "not found" outcome as a null EntityMetadata, so R8's per-annotation isolation holds
            // for this failure mode too, not just the already-handled null-metadata case.
            return null;
        }
    }

    static async Task<List<Entity>> QueryFormsAsync(
        IOrganizationServiceAsync2 service, int objectTypeCode, string formName, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("systemform")
        {
            ColumnSet = new ColumnSet("name", "formxml"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("objecttypecode", ConditionOperator.Equal, objectTypeCode),
                    new ConditionExpression("type", ConditionOperator.In, SupportedFormTypes),
                    new ConditionExpression("name", ConditionOperator.Equal, formName)
                }
            }
        };

        return await service.RetrieveAllAsync(query, cancellationToken).ConfigureAwait(false);
    }

    // R14: rooted at systemform (not solutioncomponent) since name/formxml/objecttypecode are systemform's
    // own columns. Single link to solutioncomponent filtered by solutionid — mirrors
    // WebResourceReader.GetWebResourcesForSolutionAsync, which resolves the solution once and filters
    // directly by its id rather than joining to solution by uniquename a second time.
    static async Task<List<Entity>> QuerySolutionScopedFormsAsync(
        IOrganizationServiceAsync2 service, Guid solutionId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("systemform")
        {
            ColumnSet = new ColumnSet("name", "formxml", "objecttypecode"),
            Criteria = { Conditions = { new ConditionExpression("type", ConditionOperator.In, SupportedFormTypes) } }
        };

        var componentLink = query.AddLink("solutioncomponent", "formid", "objectid", JoinOperator.Inner);
        componentLink.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
        componentLink.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, SystemFormComponentType);

        return await service.RetrieveAllAsync(query, cancellationToken).ConfigureAwait(false);
    }

    // Case-insensitive (Entity, Form) key comparer — DB-returned names and reverse-resolved logical
    // names aren't guaranteed to share the annotation's exact casing. Internal (not private) so
    // FormEventPlanner can key its own annotation-lookup dictionary the same way instead of re-deriving
    // the same case-insensitive rule via manual ToLowerInvariant() tuples.
    internal sealed class FormKeyComparer : IEqualityComparer<(string Entity, string Form)>
    {
        public static readonly FormKeyComparer Instance = new();

        public bool Equals((string Entity, string Form) x, (string Entity, string Form) y) =>
            string.Equals(x.Entity, y.Entity, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Form, y.Form, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Entity, string Form) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Entity),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Form));
    }
}
