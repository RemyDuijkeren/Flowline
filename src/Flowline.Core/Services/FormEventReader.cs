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

        var objectTypeCodes = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in resolvedAnnotations.Select(a => a.Annotation.Entity).Distinct(StringComparer.OrdinalIgnoreCase))
            objectTypeCodes[entity] = await ResolveObjectTypeCodeAsync(service, entity, cancellationToken).ConfigureAwait(false);

        var resolvedForms = new Dictionary<(string Entity, string Form), DataverseForm>();
        var formErrors = new Dictionary<(string Entity, string Form), string>();

        var pairs = resolvedAnnotations
            .Select(a => (a.Annotation.Entity, a.Annotation.Form))
            .Distinct();

        foreach (var (entity, form) in pairs)
        {
            // Entity failed to resolve — skip the form lookup, the per-annotation error is reported below.
            if (objectTypeCodes[entity] is not { } objectTypeCode)
                continue;

            var matches = await QueryFormsAsync(service, objectTypeCode, form, cancellationToken).ConfigureAwait(false);
            switch (matches.Count)
            {
                case 0:
                    formErrors[(entity, form)] = $"form '{form}' not found for entity '{entity}' (Main or Quick Create form).";
                    break;
                case 1:
                    var formEntity = matches[0];
                    resolvedForms[(entity, form)] = new DataverseForm(
                        formEntity.Id,
                        formEntity.GetAttributeValue<string>("name"),
                        entity,
                        formEntity.GetAttributeValue<string>("formxml"));
                    break;
                default:
                    formErrors[(entity, form)] = $"form '{form}' for entity '{entity}' is ambiguous — {matches.Count} systemform records matched.";
                    break;
            }
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

            var annotations = FormEventAnnotationParser.ParseAnnotations(resource.Path);
            if (annotations.Count == 0) continue;

            var content = string.IsNullOrEmpty(resource.Content)
                ? string.Empty
                : Encoding.UTF8.GetString(Convert.FromBase64String(resource.Content));

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
