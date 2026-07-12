using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Models;
using Flowline.Core.Services;

namespace Flowline.Core.Tests;

// Shared across FormEventReaderTests, FormEventServiceTests, FormEventExecutorTests, and
// FormEventPlannerTests — mocking shape and formxml-building are identical across all four.
static class FormEventTestHelpers
{
    public static string BuildFormXml(FormEventType? evt = null, IReadOnlySet<FormEventHandler>? handlers = null, IReadOnlySet<FormLibrary>? libraries = null)
    {
        var xdoc = new XDocument(new XElement("form"));
        if (evt.HasValue)
            FormXmlEventSerializer.SetHandlers(xdoc, evt.Value, handlers ?? new HashSet<FormEventHandler>());
        if (libraries != null)
            FormXmlEventSerializer.SetLibraries(xdoc, libraries);
        return xdoc.ToString();
    }

    public static void SetupSolution(this IOrganizationServiceAsync2 service, string solutionName, string prefix)
    {
        var solution = new Entity("solution", Guid.NewGuid())
        {
            ["uniquename"] = solutionName,
            ["ismanaged"] = false,
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", prefix)
        };

        service.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([solution])));
    }

    public static void SetupEntityObjectTypeCode(this IOrganizationServiceAsync2 service, string logicalName, int? objectTypeCode)
    {
        EntityMetadata? metadata = null;
        if (objectTypeCode.HasValue)
        {
            metadata = new EntityMetadata { LogicalName = logicalName };
            typeof(EntityMetadata).GetProperty("ObjectTypeCode")!.SetValue(metadata, objectTypeCode.Value);
        }

        var response = new RetrieveEntityResponse
        {
            Results = new ParameterCollection { ["EntityMetadata"] = metadata }
        };

        service.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == logicalName),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    // Mocks FormEventReader's global (unscoped) per-pair fallback query — used by R8/R8a tests to
    // simulate a form that exists in Dataverse generally but isn't a component of the current solution.
    public static void SetupSystemForms(this IOrganizationServiceAsync2 service, int objectTypeCode, string formName, params (Guid Id, string Name, string FormXml)[] forms)
    {
        var entities = forms.Select(f => new Entity("systemform", f.Id)
        {
            ["name"] = f.Name,
            ["formxml"] = f.FormXml
        }).ToList();

        service.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "systemform" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "objecttypecode" && c.Values.Contains(objectTypeCode)) &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "name" && c.Values.Contains(formName))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    // Mocks FormEventReader's solution-scoped systemform query (R14): systemform joined to
    // solutioncomponent joined to solution — distinguished from SetupSystemForms' unscoped query by the
    // presence of LinkEntities.
    //
    // Confirmed live: querying systemform.objecttypecode through the solutioncomponent link returns the
    // entity's LOGICAL NAME as a string (e.g. "contact"), not a numeric ObjectTypeCode — mocking it as a
    // number here would hide the FormatException a real push hit.
    public static void SetupSystemFormsInSolution(this IOrganizationServiceAsync2 service, params (Guid Id, string Name, string FormXml, string EntityLogicalName)[] forms)
    {
        var entities = forms.Select(f => new Entity("systemform", f.Id)
        {
            ["name"] = f.Name,
            ["formxml"] = f.FormXml,
            ["objecttypecode"] = f.EntityLogicalName
        }).ToList();

        service.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "systemform" && q.LinkEntities.Count > 0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }
}
