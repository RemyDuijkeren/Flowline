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

    public async Task SyncAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        bool save = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        // Phase 1: Get or register assembly
        var (assembly, needsUpdate) = await GetOrRegisterAssemblyAsync(service, metadata, solutionName, cancellationToken).ConfigureAwait(false);
        output.Info($"Assembly '{metadata.Name}' ({metadata.Version}) found in solution '{solutionName}'.");

        // Phase 2: Load snapshot (all Dataverse state in parallel)
        var snapshot = await _reader.LoadSnapshotAsync(service, assembly.Id, metadata, solutionName, cancellationToken).ConfigureAwait(false);

        // Phase 3: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot, metadata, assembly, solutionName);
        output.Info("Registration plan created");

        // Phase 4: Execute deletes first — must precede assembly update and upserts
        await _executor.ExecuteDeletesAsync(service, plan, solutionName, save, cancellationToken).ConfigureAwait(false);
        output.Info($"Deleted obsolete components for [bold]{metadata.Name}[/]");

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
        else
        {
            output.Skip($"Assembly '{metadata.Name}' is unchanged — skipping upload.");
        }

        // Phase 6: Execute upserts and add to solution
        await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
        await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken).ConfigureAwait(false);
    }

    async Task<(Entity entity, bool needsUpdate)> GetOrRegisterAssemblyAsync(
        IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName, CancellationToken cancellationToken = default)
    {
        var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("pluginassembly")
        {
            TopCount = 1,
            ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("pluginassemblyid", "name", "version", "description"),
            Criteria =
            {
                Conditions = { new Microsoft.Xrm.Sdk.Query.ConditionExpression("name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, metadata.Name) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existing = result.Entities.FirstOrDefault();

        if (existing == null)
        {
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

            output.Info($"[green]Added assembly for [bold]{metadata.Name}[/][/]");

            entity.Id = response.id;
            return (entity, false);
        }

        await AddSolutionComponentAsync(service, existing.Id, solutionName, cancellationToken).ConfigureAwait(false);
        var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
        return (existing, storedHash != metadata.Hash);
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
            output.Info($"[yellow]Warning:[/] Updating assembly '{assemblyName}' which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
    }

    static string? ParseStoredHash(string? description)
    {
        if (description == null) return null;
        var idx = description.IndexOf("sha256=", StringComparison.Ordinal);
        return idx < 0 ? null : description[(idx + 7)..].Split(' ')[0].Trim();
    }
}
