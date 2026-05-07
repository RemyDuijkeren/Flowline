using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class WebResourcePlanner(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    static readonly Regex ValidFilePathRegex = new(@"^[a-zA-Z0-9_.\-]+(/[a-zA-Z0-9_.\-]+)*$", RegexOptions.Compiled);

    public WebResourceSyncPlan Plan(WebResourceSyncSnapshot snapshot)
    {
        ValidateWebResourceFiles(snapshot);

        var plan = new WebResourceSyncPlan();
        var localNames = snapshot.LocalResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataverseNames = snapshot.DataverseResources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetSolutionName = snapshot.Solution.UniqueName;

        // Don't exist in this solution — create, or add to solution if already in Dataverse globally
        foreach (var name in localNames.Except(dataverseNames, StringComparer.OrdinalIgnoreCase))
        {
            var local = snapshot.LocalResources[name];

            if (snapshot.GlobalOrphans.TryGetValue(name, out var existing))
            {
                if (existing.Content != local.Content || existing.DisplayName != local.DisplayName)
                {
                    existing.Entity["content"] = local.Content;
                    existing.Entity["displayname"] = local.DisplayName;
                    existing.Entity["webresourcetype"] = new OptionSetValue((int)local.Type);
                    plan.Updates.Add(new WebResourcePlanAction(name, WebResourceAction.Update, Entity: existing.Entity, Id: existing.Id));
                }
                plan.AddsToSolution.Add(new WebResourcePlanAction(name, WebResourceAction.AddToSolution, Id: existing.Id, SolutionName: targetSolutionName));
                continue;
            }

            plan.Creates.Add(new WebResourcePlanAction(
                name,
                WebResourceAction.Create,
                Entity: ToEntity(local),
                SolutionName: targetSolutionName));
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
            remote.Entity["webresourcetype"] = new OptionSetValue((int)local.Type);

            plan.Updates.Add(new WebResourcePlanAction(
                name,
                WebResourceAction.Update,
                Entity: remote.Entity,
                Id: remote.Id));
        }

        // Exist in Dataverse, but not in local, delete or remove them
        foreach (var name in dataverseNames.Except(localNames, StringComparer.OrdinalIgnoreCase))
        {
            var remote = snapshot.DataverseResources[name];

            if (remote.Ownership is { NonDefaultUnmanagedSolutionCount: 1, IsInCurrentUnmanagedSolution: true })
            {
                plan.Deletes.Add(new WebResourcePlanAction(name, WebResourceAction.Delete, Id: remote.Id));
                continue;
            }

            if (remote.Ownership.NonDefaultUnmanagedSolutionCount > 1)
            {
                plan.RemovesFromSolution.Add(
                    new WebResourcePlanAction(name, WebResourceAction.RemoveFromSolution, Id: remote.Id, SolutionName: targetSolutionName));
                continue;
            }

            plan.Skips.Add(new WebResourcePlanAction(name, WebResourceAction.Skip, Id: remote.Id, Reason: "ownership unclear"));
        }

        return plan;
    }

    void ValidateWebResourceFiles(WebResourceSyncSnapshot snapshot)
    {
        var unknownFiles = snapshot.LocalResources.Values
                                   .Where(r => r.Type == WebResourceType.Unknown)
                                   .Select(r => r.RelativePath)
                                   .OrderBy(p => p)
                                   .ToList();

        // Web resource names may only include letters, numbers, periods, and nonconsecutive forward slash characters.
        var invalidNames = snapshot.LocalResources.Values
                                   .Where(r => !ValidFilePathRegex.IsMatch(r.RelativePath))
                                   .Select(r => r.RelativePath)
                                   .OrderBy(p => p)
                                   .ToList();

        // Silverlight/XAP is deprecated: https://learn.microsoft.com/en-us/dynamics365/customerengagement/on-premises/developer/silverlight-xap-web-resources?view=op-9-1
        var xapFiles = snapshot.LocalResources.Values
                               .Where(r => r.Type == WebResourceType.Xap)
                               .Select(r => r.RelativePath)
                               .OrderBy(p => p)
                               .ToList();

        var errorCount = unknownFiles.Count + invalidNames.Count + xapFiles.Count;
        if (errorCount <= 0) return;

        foreach (var filePath in unknownFiles)
            output.Error($"Unsupported file extension: '{filePath}'");
        foreach (var filePath in invalidNames)
            output.Error($"Invalid file name: '{filePath}'");
        foreach (var filePath in xapFiles)
            output.Error($"Silverlight/XAP is deprecated: '{filePath}'");

        throw new InvalidOperationException($"{errorCount} web resource file(s) cannot be synced.");
    }

    static Entity ToEntity(LocalWebResource local) =>
        new("webresource")
        {
            ["name"] = local.Name,
            ["displayname"] = local.DisplayName,
            ["webresourcetype"] = new OptionSetValue((int)local.Type),
            ["content"] = local.Content
        };
}
