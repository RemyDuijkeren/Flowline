using System.ServiceModel;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Configure;

/// <summary>One table that could not be published, and why.</summary>
public sealed record PublishFailure(string Table, string Detail);

/// <summary>
/// Publishes the tables whose form changes would otherwise stay invisible (KTD30).
/// </summary>
/// <remarks>
/// <b>A form change does not take effect until its table is published.</b> Confirmed live: straight after
/// the write the published row still reads active while the unpublished row reads inactive. A view needs
/// none of this, and neither does any other class the settings surface touches.
///
/// <b>The table is the smallest scope that works, and the narrower attempts fail silently.</b> The publish
/// schema has no node for a form. Its <c>dashboards</c> node is documented as taking "Guid of the
/// systemform to publish", and a dashboard is a <c>systemform</c> row, so it looks like the per-form
/// route — but passing a main form's id there is <i>accepted</i> and publishes nothing. An invented
/// <c>systemforms</c> node is accepted and ignored too. Both were tried against a live environment and
/// both left the published row unchanged, which is the trap this class exists to stay out of: a narrower
/// publish here would report success and leave the form invisible.
///
/// So the scope is <c>&lt;entities&gt;&lt;entity&gt;</c>, and it carries a cost worth being honest about:
/// it republishes every pending customization on that table, not only the form that was just switched.
/// That is wider than the write it completes. It is accepted because the alternative is a command whose
/// entire purpose is making a form change take effect leaving it not taking effect.
/// </remarks>
public static class CustomizationPublisher
{
    /// <summary>Publishes each table, and reports the ones Dataverse refused.</summary>
    /// <remarks>
    /// One request per table rather than one for all of them, so a table that fails does not take the
    /// others with it. A publish runs after the write it completes, so a failure here is never a reason
    /// to fail the run: the state change already happened and re-running the publish is safe.
    /// </remarks>
    public static async Task<IReadOnlyList<PublishFailure>> PublishTablesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<string> tables,
        CancellationToken ct)
    {
        var failures = new List<PublishFailure>();

        foreach (var table in tables)
        {
            try
            {
                await service.ExecuteAsync(new PublishXmlRequest { ParameterXml = ParameterXmlFor(table) }, ct)
                    .ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                failures.Add(new PublishFailure(table, $"Dataverse refused (error 0x{ex.Detail?.ErrorCode ?? 0:X8})."));
            }
        }

        return failures;
    }

    /// <summary>Builds the publish request for one table.</summary>
    /// <remarks>
    /// Built through <see cref="XElement"/> rather than string interpolation. A table's logical name is
    /// not free text, but this string is sent to a server as XML, and hand-concatenating markup is how a
    /// value that turns out not to be as constrained as assumed becomes an injected document.
    /// </remarks>
    internal static string ParameterXmlFor(string table) =>
        new XElement("importexportxml", new XElement("entities", new XElement("entity", table)))
            .ToString(SaveOptions.DisableFormatting);
}
