using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Services;

namespace Flowline.Core.OrphanCleanup;

// Component-type maps shared by orphan cleanup (verbose-unsupported-orphan preview) and any other
// consumer that needs to label or name a solutioncomponent.componenttype outside handler dispatch.
// Extracted from OrphanCleanupService (U1/KTD3) — same maps, same empty-on-unknown-type behavior.
public static class ComponentTypeCatalog
{
    // componenttype → backing table. Still needed by two concerns outside the handler dispatch:
    // OrphanCleanupService.ResolveNamedComponentIdsAsync's schemaName pre-diff resolution, and
    // ResolveGroupNamesAsync's fallback name resolution for a candidate no handler claims (e.g.
    // Form/View/ConnectionRole, which have no handler). Six entries also have their own copy in their
    // owning handler — not redundant, this table serves the two concerns above only.
    internal static readonly Dictionary<int, (string EntityLogicalName, string IdAttribute, string NameAttribute)> NameResolvableTypes = new()
    {
        [91] = ("pluginassembly", "pluginassemblyid", "name"),
        [90] = ("plugintype", "plugintypeid", "typename"),
        [92] = ("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "name"),
        [93] = ("sdkmessageprocessingstepimage", "sdkmessageprocessingstepimageid", "name"),
        [61] = ("webresource", "webresourceid", "name"),
        [29] = ("workflow", "workflowid", "name"),
        [60] = ("systemform", "formid", "name"),
        [26] = ("savedquery", "savedqueryid", "name"),
        [20] = ("role", "roleid", "name"),
        [63] = ("connectionrole", "connectionroleid", "name"),
        // Confirmed via learn.microsoft.com/power-apps/developer/data-platform/reference/entities/sitemap:
        // LogicalName "sitemap", PrimaryIdAttribute "sitemapid", name attribute "sitemapname".
        [62] = ("sitemap", "sitemapid", "sitemapname"),
    };

    // Manual-orphan display labels for solutioncomponent.componenttype (learn.microsoft.com/power-apps/
    // developer/data-platform/reference/entities/solutioncomponent). Not exhaustive — unmapped types
    // (e.g. env-specific 10000+ codes) are reported as unrecognized rather than guessed at. Used by
    // OrphanCleanupService.LogUnsupportedOrphansAsync for any candidate no handler claims.
    internal static readonly Dictionary<int, string> ManualTypeLabels = new()
    {
        [1]   = "Entity",
        [2]   = "Attribute",
        [3]   = "Relationship",
        [9]   = "OptionSet",
        [14]  = "EntityKey",
        [20]  = "Role",
        [24]  = "Form",
        [26]  = "View",
        [36]  = "EmailTemplate",
        [44]  = "DuplicateRule",
        [46]  = "EntityMap",
        [60]  = "Form",
        [62]  = "SiteMap",
        [63]  = "ConnectionRole",
        [66]  = "CustomControl",
        [70]  = "FieldSecurityProfile",
        [71]  = "FieldPermission",
        [95]  = "ServiceEndpoint",
        [150] = "RoutingRule",
        [152] = "SLA",
        [161] = "MobileOfflineProfile",
        [165] = "SimilarityRule",
        [166] = "DataSourceMapping",
        [208] = "ImportMap",
        [300] = "CanvasApp",
        [371] = "Connector",
        [372] = "Connector",
        [380] = "EnvironmentVariableDefinition",
        [381] = "EnvironmentVariableValue",
    };

    // Shared by every componentType-group name-resolution loop remaining after handler dispatch — a type
    // with no NameResolvableTypes entry resolves to an empty map.
    internal static Task<Dictionary<Guid, string>> ResolveGroupNamesAsync(
        IOrganizationServiceAsync2 service,
        int componentType,
        IEnumerable<Guid> ids,
        CancellationToken ct) =>
        NameResolvableTypes.TryGetValue(componentType, out var lookup)
            ? EntityNameLookup.GetEntityNamesAsync(service, lookup.EntityLogicalName, lookup.IdAttribute, lookup.NameAttribute, ids, ct)
            : Task.FromResult(new Dictionary<Guid, string>());
}
