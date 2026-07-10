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
    public static string BuildFormXml(FormEventType? evt = null, IReadOnlySet<FormHandler>? handlers = null, IReadOnlySet<FormLibraryEntry>? libraries = null)
    {
        var xdoc = new XDocument(new XElement("form"));
        if (evt.HasValue)
            FormXmlEventSerializer.SetHandlers(xdoc, evt.Value, handlers ?? new HashSet<FormHandler>());
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
}
