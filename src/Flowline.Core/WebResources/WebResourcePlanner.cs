using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.WebResources;

public class WebResourcePlanner(IAnsiConsole console)
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
                var existingDeps = DependencyXmlSerializer.Deserialize(existing.DependencyXml);
                var existingByName = ToDictByName(existingDeps);
                var desiredDeps = BuildDesiredSet(local.DependsOn, existingByName, snapshot);

                var depsChangedGlobal = DependenciesDiffer(desiredDeps, existingDeps);
                if (existing.Content != local.Content || existing.DisplayName != local.DisplayName || depsChangedGlobal)
                {
                    existing.Entity["content"] = local.Content;
                    existing.Entity["displayname"] = local.DisplayName;
                    existing.Entity["webresourcetype"] = new OptionSetValue((int)local.Type);
                    if (depsChangedGlobal)
                        existing.Entity["dependencyxml"] = DependencyXmlSerializer.Serialize(desiredDeps);
                    var reasonGlobal = DescribeChanges(existing.Content != local.Content, existing.DisplayName != local.DisplayName, depsChangedGlobal);
                    plan.Updates.Add(new WebResourcePlanAction(name, WebResourceAction.Update, Entity: existing.Entity, Id: existing.Id, Reason: reasonGlobal));
                }
                plan.AddsToSolution.Add(new WebResourcePlanAction(name, WebResourceAction.AddToSolution, Id: existing.Id, SolutionName: targetSolutionName));
                continue;
            }

            var entity = ToEntity(local);
            if (local.DependsOn.Count > 0)
            {
                var desiredDeps = BuildDesiredSet(local.DependsOn, [], snapshot);
                entity["dependencyxml"] = DependencyXmlSerializer.Serialize(desiredDeps);
            }

            plan.Creates.Add(new WebResourcePlanAction(
                name,
                WebResourceAction.Create,
                Entity: entity,
                SolutionName: targetSolutionName));
        }

        // Exist in both, update them if needed
        foreach (var name in localNames.Intersect(dataverseNames, StringComparer.OrdinalIgnoreCase))
        {
            var local = snapshot.LocalResources[name];
            var remote = snapshot.DataverseResources[name];

            var currentDeps = DependencyXmlSerializer.Deserialize(remote.DependencyXml);
            var currentByName = ToDictByName(currentDeps);
            var desiredDeps = BuildDesiredSet(local.DependsOn, currentByName, snapshot);
            var depsChanged = DependenciesDiffer(desiredDeps, currentDeps);

            var contentChanged = remote.Content != local.Content;
            var displayNameChanged = remote.DisplayName != local.DisplayName;
            if (!contentChanged && !displayNameChanged && !depsChanged)
                continue;

            remote.Entity["content"] = local.Content;
            remote.Entity["displayname"] = local.DisplayName;
            remote.Entity["webresourcetype"] = new OptionSetValue((int)local.Type);

            if (depsChanged)
                remote.Entity["dependencyxml"] = DependencyXmlSerializer.Serialize(desiredDeps);
            else
                remote.Entity.Attributes.Remove("dependencyxml");

            plan.Updates.Add(new WebResourcePlanAction(
                name,
                WebResourceAction.Update,
                Entity: remote.Entity,
                Id: remote.Id,
                Reason: DescribeChanges(contentChanged, displayNameChanged, depsChanged)));
        }

        // Exist in Dataverse, but not in local, delete or remove them
        foreach (var name in dataverseNames.Except(localNames, StringComparer.OrdinalIgnoreCase))
        {
            var remote = snapshot.DataverseResources[name];

            if (remote.Ownership is { NonDefaultUnmanagedSolutionCount: 1, IsInCurrentUnmanagedSolution: true, HasManagedSolutionReference: false })
            {
                plan.Deletes.Add(new WebResourcePlanAction(name, WebResourceAction.Delete, Id: remote.Id));
                continue;
            }

            // A managed solution reference means some other product (e.g. Field Service) owns the record —
            // only this unmanaged solution's link is ours to drop, never the record itself.
            if (remote.Ownership.NonDefaultUnmanagedSolutionCount > 1 ||
                (remote.Ownership is { IsInCurrentUnmanagedSolution: true, HasManagedSolutionReference: true }))
            {
                plan.RemovesFromSolution.Add(
                    new WebResourcePlanAction(name, WebResourceAction.RemoveFromSolution, Id: remote.Id, SolutionName: targetSolutionName));
                continue;
            }

            plan.Skips.Add(new WebResourcePlanAction(name, WebResourceAction.Skip, Id: remote.Id, Reason: "ownership unclear"));
        }

        return plan;
    }

    IReadOnlySet<DependencyLibrary> BuildDesiredSet(
        IReadOnlyList<string> dependsOn,
        Dictionary<string, DependencyLibrary> existingByName,
        WebResourceSyncSnapshot snapshot)
    {
        if (dependsOn.Count == 0)
            return new HashSet<DependencyLibrary>();

        var result = new HashSet<DependencyLibrary>();
        foreach (var rawName in dependsOn.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = ResolveQualifiedName(rawName, snapshot);
            if (existingByName.TryGetValue(name, out var existing))
                result.Add(existing);
            else
                result.Add(new DependencyLibrary(name, ResolveDisplayName(name, snapshot), Guid.NewGuid()));
        }
        return result;
    }

    // Maker Portal stores the fully-qualified webresource name (e.g. "av_ns/example1.js") in Library@name,
    // not the bare filename typed in a // flowline:depends annotation — qualify it so dependencies
    // added via Flowline resolve the same way as ones added manually in the Maker Portal.
    string ResolveQualifiedName(string rawName, WebResourceSyncSnapshot snapshot)
    {
        if (snapshot.LocalResources.ContainsKey(rawName) || snapshot.DataverseResources.ContainsKey(rawName))
            return rawName;

        var suffix = "/" + rawName;
        var matches = snapshot.LocalResources.Keys
            .Concat(snapshot.DataverseResources.Keys)
            .Where(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count > 1)
            console.Warning($"'{rawName}': multiple web resources match this dependency name — keeping unqualified. Use a folder-qualified name (e.g. 'av_ns/{rawName}') in // flowline:depends to disambiguate.");

        return rawName;
    }

    static string ResolveDisplayName(string logicalName, WebResourceSyncSnapshot snapshot)
    {
        if (snapshot.LocalResources.TryGetValue(logicalName, out var local))
            return local.DisplayName;
        if (snapshot.DataverseResources.TryGetValue(logicalName, out var remote) && remote.DisplayName != null)
            return remote.DisplayName;
        var lastSlash = logicalName.LastIndexOf('/');
        return lastSlash >= 0 ? logicalName[(lastSlash + 1)..] : logicalName;
    }

    static string DescribeChanges(bool contentChanged, bool displayNameChanged, bool depsChanged)
    {
        var parts = new List<string>();
        if (contentChanged) parts.Add("content");
        if (displayNameChanged) parts.Add("displayname");
        if (depsChanged) parts.Add("dependencies");
        return string.Join(", ", parts);
    }

    static bool DependenciesDiffer(IReadOnlySet<DependencyLibrary> desired, IReadOnlySet<DependencyLibrary> current)
    {
        if (desired.Count != current.Count) return true;
        var currentNames = current.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return desired.Any(l => !currentNames.Contains(l.Name));
    }

    static Dictionary<string, DependencyLibrary> ToDictByName(IReadOnlySet<DependencyLibrary> set) =>
        set.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

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
            console.Error($"Unsupported file extension: '{filePath}' — metadata lookup and content sniffing were both tried and neither resolved a type.");
        foreach (var filePath in invalidNames)
            console.Error($"Invalid file name: '{filePath}'");
        foreach (var filePath in xapFiles)
            console.Error($"Silverlight/XAP is deprecated: '{filePath}'");

        throw new FlowlineException(ExitCode.ValidationFailed, $"{errorCount} web resource file(s) cannot be synced.");
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
