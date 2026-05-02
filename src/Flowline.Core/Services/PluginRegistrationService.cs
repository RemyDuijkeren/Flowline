using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class PluginRegistrationService(IFlowlineOutput output)
{
    const string FlowlineMarker = "[flowline]";

    readonly PluginRegistrationReader _reader   = new();
    readonly RegistrationPlanner      _planner  = new(output);
    readonly RegistrationPlanExecutor _executor = new(output);

    public async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        // Phase 1: Get or register assembly
        var (assembly, needsUpdate) = await GetOrRegisterAssemblyAsync(service, metadata, solutionName, runMode, cancellationToken).ConfigureAwait(false);
        output.Info($"[green]Assembly: [bold]{metadata.Name}[/] ({metadata.Version})[/]");

        // Phase 2: Load snapshot (all Dataverse state in parallel)
        var snapshot = await _reader.LoadSnapshotAsync(service, assembly.Id, metadata, solutionName, cancellationToken).ConfigureAwait(false);

        // Phase 3: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot, metadata, assembly, solutionName);
        output.Info("[green]Registration plan ready[/]");

        // Dry-run: print preview and return without making any changes
        if (runMode == RunMode.DryRun)
        {
            WriteDryRunSummary(metadata, needsUpdate, plan);
            return;
        }

        // Phase 4: Execute the deletes first — must precede assembly update and upserts
        await _executor.ExecuteDeletesAsync(service, plan, solutionName, runMode == RunMode.Save, cancellationToken).ConfigureAwait(false);
        if (plan.TotalDeletes > 0) output.Info($"[green]{plan.TotalDeletes} stale component(s) deleted[/]");

        // Phase 5: Update assembly content — must happen before new plugin types are registered
        if (needsUpdate)
        {
            await WarnIfAssemblyInOtherSolutionsAsync(service, assembly.Id, solutionName, metadata.Name, cancellationToken).ConfigureAwait(false);
            assembly["content"]     = Convert.ToBase64String(metadata.Content);
            assembly["version"]     = metadata.Version;
            assembly["description"] = $"{FlowlineMarker} sha256={metadata.Hash}";
            await service.UpdateAsync(assembly, cancellationToken).ConfigureAwait(false);
            output.Info($"[green]Updated assembly content for [bold]{metadata.Name}[/][/]");
        }

        // Phase 6: Execute upserts and add to solution
        await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
        if (plan.TotalUpserts > 0) output.Info($"[green]{plan.TotalUpserts} component(s) synced[/]");
        await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken).ConfigureAwait(false);
    }

    async Task<(Entity entity, bool needsUpdate)> GetOrRegisterAssemblyAsync(
        IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName, RunMode runMode, CancellationToken cancellationToken = default)
    {
        var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("pluginassembly")
        {
            TopCount = 1,
            ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description"),
            Criteria =
            {
                Conditions = { new Microsoft.Xrm.Sdk.Query.ConditionExpression("name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, metadata.Name) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existing = result.Entities.FirstOrDefault();

        if (existing == null)
        {
            if (runMode == RunMode.DryRun)
            {
                output.Skip($"Assembly '{metadata.Name}' — would create");
                // Return a dummy entity so that the caller can continue with the dry-run
                return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false);
            }

            var entity = new Entity("pluginassembly")
            {
                ["name"]          = metadata.Name,
                ["content"]       = Convert.ToBase64String(metadata.Content),
                ["version"]       = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2), // 2 = Sandbox (cloud only)
                ["description"]   = $"{FlowlineMarker} sha256={metadata.Hash}"
            };

            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken).ConfigureAwait(false);

            output.Info($"[green]Assembly [bold]{metadata.Name}[/] added[/]");

            entity.Id = response.id;
            return (entity, false);
        }

        var registeredPkt     = existing.GetAttributeValue<string>("publickeytoken");
        var registeredCulture = existing.GetAttributeValue<string>("culture") ?? "neutral";
        var registeredVersion = existing.GetAttributeValue<string>("version");

        bool pktChanged        = !string.Equals(registeredPkt, metadata.PublicKeyToken, StringComparison.OrdinalIgnoreCase);
        bool cultureChanged    = !string.Equals(registeredCulture, metadata.Culture, StringComparison.OrdinalIgnoreCase);
        bool majorMinorChanged = HasMajorOrMinorVersionChange(registeredVersion, metadata.Version);

        if (pktChanged || cultureChanged || majorMinorChanged)
        {
            var reasons = new List<string>();
            if (pktChanged)        reasons.Add($"public key token ({registeredPkt ?? "null"} -> {metadata.PublicKeyToken ?? "null"})");
            if (cultureChanged)    reasons.Add($"culture ({registeredCulture} -> {metadata.Culture})");
            if (majorMinorChanged) reasons.Add($"major/minor version ({registeredVersion} -> {metadata.Version})");
            var reason = string.Join(", ", reasons);

            switch (runMode)
            {
                case RunMode.Save:
                    output.Error($"Assembly '{metadata.Name}' identity changed ({reason}) — Dataverse needs a delete and recreate. Re-run without --save to apply it, or use --dry-run to preview.");
                    throw new InvalidOperationException($"Assembly '{metadata.Name}' identity changed ({reason}). Cannot continue in save mode — re-run without --save to apply or use --dry-run to preview changes.");
                case RunMode.DryRun:
                    output.Skip($"Assembly '{metadata.Name}' identity changed ({reason}) — would delete and recreate");
                    // Return a dummy entity so that the caller can continue with the dry-run
                    return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false);
                case RunMode.Normal:
                    output.Warning($"Assembly '{metadata.Name}' identity changed ({reason}) — deleting and recreating plugin registrations.");
                    await service.DeleteAsync("pluginassembly", existing.Id, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(runMode), runMode, null);
            }

            var freshEntity = new Entity("pluginassembly")
            {
                ["name"]          = metadata.Name,
                ["content"]       = Convert.ToBase64String(metadata.Content),
                ["version"]       = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2),
                ["description"]   = $"{FlowlineMarker} sha256={metadata.Hash}"
            };

            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = freshEntity, ["SolutionUniqueName"] = solutionName },
                cancellationToken).ConfigureAwait(false);

            freshEntity.Id = response.id;
            output.Info($"[green]Assembly [bold]{metadata.Name}[/] recreated[/]");
            return (freshEntity, false);
        }

        await AddSolutionComponentAsync(service, existing.Id, solutionName, cancellationToken).ConfigureAwait(false);
        var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
        return (existing, storedHash != metadata.Hash);
    }

    void WriteDryRunSummary(PluginAssemblyMetadata metadata, bool needsUpdate, RegistrationPlan plan)
    {
        if (needsUpdate)
            output.Skip($"Assembly '{metadata.Name} ({metadata.Version})' — would update content");

        foreach (var s in plan.PluginTypes.Deletes.Keys)   output.Skip($"Plugin type '{s}' — would delete");
        foreach (var s in plan.Steps.Deletes.Keys)         output.Skip($"Step '{s}' — would delete");
        foreach (var s in plan.Images.Deletes.Keys)        output.Skip($"Image '{s}' — would delete");
        foreach (var s in plan.CustomApis.Deletes.Keys)    output.Skip($"Custom API '{s}' — would delete");
        foreach (var s in plan.RequestParams.Deletes.Keys) output.Skip($"Request parameter '{s}' — would delete");
        foreach (var s in plan.ResponseProps.Deletes.Keys) output.Skip($"Response property '{s}' — would delete");

        foreach (var (s, ups) in plan.PluginTypes.Upserts)   output.Skip($"Plugin type '{s}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var (s, ups) in plan.Steps.Upserts)         output.Skip($"Step '{s}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var (s, ups) in plan.Images.Upserts)        output.Skip($"Image '{s}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var (s, ups) in plan.CustomApis.Upserts)    output.Skip($"Custom API '{s}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var (s, ups) in plan.RequestParams.Upserts) output.Skip($"Request parameter '{s}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var (s, ups) in plan.ResponseProps.Upserts) output.Skip($"Response property '{s}' — would {(ups.IsCreate ? "create" : "update")}");

        var creates = plan.PluginTypes.Upserts.Values.Count(u => u.IsCreate)
                      + plan.Steps.Upserts.Values.Count(u => u.IsCreate)
                      + plan.CustomApis.Upserts.Values.Count(u => u.IsCreate)
                      + plan.Images.Upserts.Values.Count(u => u.IsCreate)
                      + plan.RequestParams.Upserts.Values.Count(u => u.IsCreate)
                      + plan.ResponseProps.Upserts.Values.Count(u => u.IsCreate);
        var updates = plan.TotalUpserts - creates;

        output.Info($"[green]Dry run: {plan.TotalDeletes} delete(s), {creates} create(s), {updates} update(s). Run without --dry-run to apply.[/]");
    }

    async Task AddSolutionComponentAsync(IOrganizationServiceAsync2 service, Guid assemblyId, string solutionName, CancellationToken cancellationToken)
    {
        var request = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"]               = assemblyId,
            ["ComponentType"]             = 91, // PluginAssembly
            ["SolutionUniqueName"]        = solutionName,
            ["AddRequiredComponents"]     = false,
            ["DoNotIncludeSubcomponents"] = false
        };
        await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    async Task WarnIfAssemblyInOtherSolutionsAsync(IOrganizationServiceAsync2 service, Guid assemblyId, string currentSolutionName, string assemblyName, CancellationToken cancellationToken)
    {
        var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("solutioncomponent")
        {
            ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(false),
            Criteria =
            {
                Conditions =
                {
                    new Microsoft.Xrm.Sdk.Query.ConditionExpression("componenttype", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, 91),
                    new Microsoft.Xrm.Sdk.Query.ConditionExpression("objectid", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, assemblyId)
                }
            },
            LinkEntities =
            {
                new Microsoft.Xrm.Sdk.Query.LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", Microsoft.Xrm.Sdk.Query.JoinOperator.Inner)
                {
                    Columns = new Microsoft.Xrm.Sdk.Query.ColumnSet("uniquename"),
                    EntityAlias = "sol"
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var otherSolutions = result.Entities
            .Select(e => e.GetAttributeValue<AliasedValue>("sol.uniquename")?.Value as string)
            .Where(name =>
                !string.IsNullOrWhiteSpace(name) &&
                !string.Equals(name, currentSolutionName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        if (otherSolutions.Count > 0)
            output.Warning($"Updating assembly '{assemblyName}' which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
    }

    static string? ParseStoredHash(string? description)
    {
        if (description == null) return null;
        var idx = description.IndexOf("sha256=", StringComparison.Ordinal);
        return idx < 0 ? null : description[(idx + 7)..].Split(' ')[0].Trim();
    }

    internal static bool HasMajorOrMinorVersionChange(string? registered, string local)
    {
        if (string.IsNullOrWhiteSpace(registered)) return false;
        if (!Version.TryParse(registered, out var reg)) return false;
        if (!Version.TryParse(local, out var loc))      return false;
        return reg.Major != loc.Major || reg.Minor != loc.Minor;
    }
}
