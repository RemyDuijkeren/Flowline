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

    public async Task<FormEventSnapshot> LoadSnapshotAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
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

        var resolvedAnnotations = CollectAnnotations(webResourceSnapshot.LocalResources.Values);

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

        // Reverse cache (KTD11's load-bearing gap): ObjectTypeCode -> logical name, seeded from the
        // forward direction above so an entity already resolved via an annotation never triggers a
        // redundant RetrieveAllEntities round-trip.
        var entityByCode = new Dictionary<int, string>();
        foreach (var (entity, code) in objectTypeCodes)
            if (code is int c) entityByCode.TryAdd(c, entity);

        // R14: every systemform that's a component of this solution, not just forms named by a current
        // annotation. Filters directly on solutioncomponent.solutionid using the id WebResourceReader
        // already resolved above — no second join to solution by uniquename needed (mirrors
        // WebResourceReader.GetWebResourcesForSolutionAsync's single-link shape).
        var solutionFormEntities = await QuerySolutionScopedFormsAsync(service, webResourceSnapshot.Solution.Id, cancellationToken).ConfigureAwait(false);

        var missingCodes = solutionFormEntities
            .Select(e => e.GetAttributeValue<int>("objecttypecode"))
            .Distinct()
            .Where(code => !entityByCode.ContainsKey(code))
            .ToList();

        if (missingCodes.Count > 0)
        {
            var resolvedNames = await ResolveEntityLogicalNamesAsync(service, missingCodes, cancellationToken).ConfigureAwait(false);
            foreach (var (code, name) in resolvedNames)
                entityByCode[code] = name;
        }

        var solutionForms = new List<(Guid Id, string Name, string EntityLogicalName, string FormXml)>();
        foreach (var formEntity in solutionFormEntities)
        {
            var code = formEntity.GetAttributeValue<int>("objecttypecode");
            // A systemform's objecttypecode should always resolve to a real entity — skip defensively
            // rather than throw, since a resolution gap here isn't something a bad annotation caused.
            // Surfaced as a warning (not silent) because a silently skipped solution-component form is
            // exactly the R14 orphan-detection gap this unit exists to close.
            if (!entityByCode.TryGetValue(code, out var entityLogicalName))
            {
                console.Warning($"systemform '{formEntity.GetAttributeValue<string>("name")}' ({formEntity.Id}) has objecttypecode {code}, which did not resolve to an entity logical name — skipped from form-event resolution.");
                continue;
            }

            solutionForms.Add((
                formEntity.Id,
                formEntity.GetAttributeValue<string>("name"),
                entityLogicalName,
                formEntity.GetAttributeValue<string>("formxml")));
        }

        var resolvedForms = new Dictionary<(string Entity, string Form), DataverseForm>(FormKeyComparer.Instance);
        var ambiguousCounts = new Dictionary<(string Entity, string Form), int>(FormKeyComparer.Instance);

        foreach (var group in solutionForms.GroupBy(f => (f.EntityLogicalName, f.Name), FormKeyComparer.Instance))
        {
            var matches = group.ToList();
            if (matches.Count == 1)
                resolvedForms[group.Key] = new DataverseForm(matches[0].Id, matches[0].Name, matches[0].EntityLogicalName, matches[0].FormXml);
            else
                // Ambiguous within this solution — can't be represented by the unique (Entity, Form) key.
                // Only surfaced as an error below when an annotation actually targets it; an ambiguous
                // orphan form with no annotation is simply absent from Forms (rare edge case).
                ambiguousCounts[group.Key] = matches.Count;
        }

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
                0 => (Pair: pair, Error: $"form '{form}' not found for entity '{entity}' (Main or Quick Create form)."),
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
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Form event annotations failed to resolve:\n" + string.Join("\n", errors));

        return new FormEventSnapshot(validAnnotations.AsReadOnly(), trackedLibraryNames, resolvedForms.AsReadOnly());
    }

    static List<ResolvedFormEventAnnotation> CollectAnnotations(IEnumerable<LocalWebResource> localResources)
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

            var annotations = FormEventAnnotationParser.ParseAnnotations(content.Split('\n'));
            if (annotations.Count == 0) continue;

            foreach (var annotation in annotations)
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

    // KTD11's load-bearing gap: the reverse of ResolveObjectTypeCodeAsync. One bulk metadata request for
    // every needed code rather than one RetrieveEntity per orphan form's entity.
    static async Task<Dictionary<int, string>> ResolveEntityLogicalNamesAsync(
        IOrganizationServiceAsync2 service, IReadOnlyCollection<int> objectTypeCodes, CancellationToken cancellationToken)
    {
        var request = new RetrieveAllEntitiesRequest
        {
            EntityFilters = EntityFilters.Entity,
            RetrieveAsIfPublished = false
        };
        var response = (RetrieveAllEntitiesResponse)await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        var wanted = objectTypeCodes.ToHashSet();
        var result = new Dictionary<int, string>();
        foreach (var metadata in response.EntityMetadata ?? [])
        {
            if (metadata.ObjectTypeCode is { } code && wanted.Contains(code) && metadata.LogicalName is not null)
                result[code] = metadata.LogicalName;
        }
        return result;
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
