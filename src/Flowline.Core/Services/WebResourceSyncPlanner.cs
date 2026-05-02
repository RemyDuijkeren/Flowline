using Microsoft.Xrm.Sdk;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class WebResourceSyncPlanner(IFlowlineOutput output)
{
    public WebResourceSyncPlan Plan(WebResourceSyncSnapshot snapshot, RunMode runMode)
    {
        var plan = new WebResourceSyncPlan();
        var localNames = snapshot.LocalResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataverseNames = snapshot.DataverseResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetSolutionName = snapshot.PatchSolution?.UniqueName ?? snapshot.BaseSolution.UniqueName;

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
            if (local.SilverlightVersion != null)
                remote.Entity["silverlightversion"] = local.SilverlightVersion;

            if (snapshot.PatchSolution != null && !remote.IsInPatch)
            {
                plan.UpdatesAndAddsToPatch[name] = new WebResourcePlanAction(
                    name,
                    WebResourceAction.UpdateAndAddToPatchSolution,
                    Entity: remote.Entity,
                    Id: remote.Id,
                    SolutionName: snapshot.PatchSolution.UniqueName);
                continue;
            }

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

            if (runMode == RunMode.Save)
            {
                plan.Skips[name] = new WebResourcePlanAction(name, WebResourceAction.Skip, Id: remote.Id, Reason: "--save");
                continue;
            }

            if (remote.Ownership.IsManagedOnly)
            {
                plan.Skips[name] = new WebResourcePlanAction(name, WebResourceAction.Skip, Id: remote.Id, Reason: "managed solution");
                continue;
            }

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

    static Entity ToEntity(LocalWebResource local)
    {
        var entity = new Entity("webresource")
        {
            ["name"] = local.Name,
            ["displayname"] = local.DisplayName,
            ["webresourcetype"] = new OptionSetValue(local.Type),
            ["content"] = local.Content
        };

        if (local.SilverlightVersion != null)
            entity["silverlightversion"] = local.SilverlightVersion;

        return entity;
    }
}
