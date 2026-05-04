using Microsoft.Xrm.Sdk;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class WebResourcePlanner(IFlowlineOutput output)
{
    public WebResourceSyncPlan Plan(WebResourceSyncSnapshot snapshot)
    {
        var plan = new WebResourceSyncPlan();
        var localNames = snapshot.LocalResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataverseNames = snapshot.DataverseResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetSolutionName = snapshot.Solution.UniqueName;

        output.Verbose($"Found {snapshot.DataverseResources.Count} web resource(s) in Dataverse.");
        foreach (var name in dataverseNames) output.Verbose($"- {name}");
        output.Verbose($"Found {snapshot.LocalResources.Count} local web resource(s).");
        foreach (var name in localNames) output.Verbose($"- {name}");

        // Don't exist in Dataverse, create them
        foreach (var name in localNames.Except(dataverseNames, StringComparer.OrdinalIgnoreCase))
        {
            var local = snapshot.LocalResources[name];
            plan.Creates[name] = new WebResourcePlanAction(
                name,
                WebResourceAction.Create,
                Entity: ToEntity(local),
                SolutionName: targetSolutionName);
        }

        // Exist in both, update them if needed
        foreach (var name in localNames.Intersect(dataverseNames, StringComparer.OrdinalIgnoreCase))
        {
            // Compare content and display name
            var local = snapshot.LocalResources[name];
            var remote = snapshot.DataverseResources[name];
            if (remote.Content == local.Content && remote.DisplayName == local.DisplayName)
                continue;

            remote.Entity["content"] = local.Content;
            remote.Entity["displayname"] = local.DisplayName;
            remote.Entity["webresourcetype"] = new OptionSetValue(local.Type);

            plan.Updates[name] = new WebResourcePlanAction(
                name,
                WebResourceAction.Update,
                Entity: remote.Entity,
                Id: remote.Id);
        }

        // Exist in Dataverse, but not in local, delete or remove them
        foreach (var name in dataverseNames.Except(localNames, StringComparer.OrdinalIgnoreCase))
        {
            var remote = snapshot.DataverseResources[name];

            if (remote.Ownership is { NonDefaultUnmanagedSolutionCount: 1, IsInCurrentUnmanagedSolution: true })
            {
                plan.Deletes[name] = new WebResourcePlanAction(name, WebResourceAction.Delete, Id: remote.Id);
                continue;
            }

            if (remote.Ownership.NonDefaultUnmanagedSolutionCount > 1)
            {
                plan.RemovesFromSolution[name] =
                    new WebResourcePlanAction(name, WebResourceAction.RemoveFromSolution, Id: remote.Id, SolutionName: targetSolutionName);
                continue;
            }

            plan.Skips[name] = new WebResourcePlanAction(name, WebResourceAction.Skip, Id: remote.Id, Reason: "ownership unclear");
        }

        return plan;
    }

    static Entity ToEntity(LocalWebResource local) =>
        new("webresource")
        {
            ["name"] = local.Name,
            ["displayname"] = local.DisplayName,
            ["webresourcetype"] = new OptionSetValue(local.Type),
            ["content"] = local.Content
        };
}
