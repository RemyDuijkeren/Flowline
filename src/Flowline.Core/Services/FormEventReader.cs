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

        var resolvedAnnotations = CollectAnnotations(webResourceSnapshot.LocalResources.Values);
        if (resolvedAnnotations.Count == 0)
            return new FormEventSnapshot([], new Dictionary<(string, string), DataverseForm>());

        // Independent per-entity/per-form Dataverse lookups — run concurrently rather than one round-trip
        // at a time, bounded so a project with many entities/forms doesn't fan out unbounded requests.
        using var gate = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);

        var entities = resolvedAnnotations.Select(a => a.Annotation.Entity).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var objectTypeCodeResults = await Task.WhenAll(entities.Select(async entity =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return (Entity: entity, Code: await ResolveObjectTypeCodeAsync(service, entity, cancellationToken).ConfigureAwait(false)); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        var objectTypeCodes = objectTypeCodeResults.ToDictionary(r => r.Entity, r => r.Code, StringComparer.OrdinalIgnoreCase);

        var resolvedForms = new Dictionary<(string Entity, string Form), DataverseForm>();
        var formErrors = new Dictionary<(string Entity, string Form), string>();

        var pairs = resolvedAnnotations
            .Select(a => (a.Annotation.Entity, a.Annotation.Form))
            .Distinct()
            .ToList();

        var formLookups = await Task.WhenAll(pairs.Select(async pair =>
        {
            var (entity, form) = pair;

            // Entity failed to resolve — skip the form lookup, the per-annotation error is reported below.
            if (objectTypeCodes[entity] is not { } objectTypeCode)
                return (Pair: pair, Form: (DataverseForm?)null, Error: (string?)null);

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            List<Entity> matches;
            try { matches = await QueryFormsAsync(service, objectTypeCode, form, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }

            return matches.Count switch
            {
                0 => (Pair: pair, Form: null, Error: $"form '{form}' not found for entity '{entity}' (Main or Quick Create form)."),
                1 => (Pair: pair, Form: new DataverseForm(
                    matches[0].Id,
                    matches[0].GetAttributeValue<string>("name"),
                    entity,
                    matches[0].GetAttributeValue<string>("formxml")), Error: null),
                _ => (Pair: pair, Form: null, Error: $"form '{form}' for entity '{entity}' is ambiguous — {matches.Count} systemform records matched.")
            };
        })).ConfigureAwait(false);

        foreach (var (pair, form, error) in formLookups)
        {
            if (form is not null) resolvedForms[pair] = form;
            else if (error is not null) formErrors[pair] = error;
        }

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

        return new FormEventSnapshot(validAnnotations.AsReadOnly(), resolvedForms.AsReadOnly());
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
}
