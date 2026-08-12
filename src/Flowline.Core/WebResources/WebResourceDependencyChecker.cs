using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Flowline.Core.Models;
using Flowline.Core.OrphanCleanup;

namespace Flowline.Core.WebResources;

// R1/R2: before a web resource delete or remove-from-solution runs, ask Dataverse what still
// depends on it (RetrieveDependenciesForDelete, component type 61). KTD3/R11: a fault on one
// resource's lookup degrades that resource to "unchecked" — Dependents stays null, distinct from an
// empty list — and never aborts the others.
public static class WebResourceDependencyChecker
{
    const int WebResourceComponentType = 61;

    // Mirrors WebResourceReader.MaxOwnershipParallelism (same cap, same org, same rationale — bound
    // the fan-out so a large delete batch doesn't trip Dataverse's per-user service-protection limits).
    const int MaxParallelism = 8;

    public static async Task<IReadOnlyList<WebResourceDependencyResult>> CheckAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> webResourceIds,
        CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(MaxParallelism);

        var tasks = webResourceIds.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CheckOneAsync(service, id, cancellationToken).ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault>)
            {
                return new WebResourceDependencyResult(id, null);
            }
            // Mirrors MissingComponentCheckService's filter: a client-side timeout surfaces as
            // OperationCanceledException without the caller's token signalling, and that must still
            // classify as "check couldn't run" for this resource, not real cancellation.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new WebResourceDependencyResult(id, null);
            }
            finally { gate.Release(); }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    static async Task<WebResourceDependencyResult> CheckOneAsync(
        IOrganizationServiceAsync2 service, Guid webResourceId, CancellationToken cancellationToken)
    {
        var request = new RetrieveDependenciesForDeleteRequest
        {
            ComponentType = WebResourceComponentType,
            ObjectId = webResourceId
        };
        var response = (RetrieveDependenciesForDeleteResponse)await service
            .ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        var records = response.EntityCollection.Entities
            .Select(e => new
            {
                Type = e.GetAttributeValue<OptionSetValue>("dependentcomponenttype")?.Value ?? 0,
                Label = e.FormattedValues.TryGetValue("dependentcomponenttype", out var label) ? label : null,
                ObjectId = e.GetAttributeValue<Guid>("dependentcomponentobjectid")
            })
            .ToList();

        // R4 step 3: one name lookup per distinct component type present on this resource's
        // dependents, not one per dependent record.
        //
        // FIX 4: this is cosmetic name enrichment, not the primary answer — the primary
        // RetrieveDependenciesForDelete request above already succeeded, so a fault here (e.g.
        // EntityNameLookup's deterministic >2000-id InvalidOperationException for a heavily-shared
        // library) must not collapse an already-retrieved dependent list to "unchecked". Each type's
        // lookup gets its own try/catch, falling back to an empty map for just that type — the
        // nameless TypeLabel + ObjectId render path already exists for exactly this case.
        var namesByType = new Dictionary<int, Dictionary<Guid, string>>();
        foreach (var type in records.Select(r => r.Type).Distinct())
        {
            try
            {
                namesByType[type] = await ComponentTypeCatalog.ResolveGroupNamesAsync(
                    service, type, records.Where(r => r.Type == type).Select(r => r.ObjectId), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                namesByType[type] = [];
            }
        }

        var dependents = records
            .Select(r => new WebResourceDependent(
                // KTD4: FormattedValues first, ManualTypeLabels fallback — without the fallback, an
                // absent formatted value renders every dependent as a bare component-type number.
                // ponytail: a type with neither a formatted value nor a ManualTypeLabels entry still
                // falls through to the raw number (r.Type.ToString()) — add the type to ManualTypeLabels
                // if that turns out to be a type callers commonly see.
                r.Label ?? (ComponentTypeCatalog.ManualTypeLabels.TryGetValue(r.Type, out var label) ? label : r.Type.ToString()),
                namesByType[r.Type].GetValueOrDefault(r.ObjectId),
                r.ObjectId))
            .ToList();

        return new WebResourceDependencyResult(webResourceId, dependents);
    }
}
