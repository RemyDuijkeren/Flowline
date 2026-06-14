using System.Text.RegularExpressions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class WebResourceReader(IAnsiConsole output)
{
    const int WebResourceComponentType = 61;
    const string DefaultSolutionUniqueName = "Default";
    // Publisher prefixes are always lowercase: ^[a-z][a-z0-9]*_
    static readonly Regex VerbatimPrefixRegex = new(@"^[a-z][a-z0-9]*_", RegexOptions.Compiled);
    static readonly Regex ResxLcidSuffixRegex = new(@"\.\d{4}\.resx$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    readonly SolutionReader _solutionReader = new();

    public async Task<WebResourceSyncSnapshot> LoadSnapshotAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        var baseSolution = await _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken).ConfigureAwait(false);

        var baseResourcesTask = GetWebResourcesForSolutionAsync(service, baseSolution.Id, cancellationToken);
        var localResourcesTask = Task.Run(() => GetLocalWebResources(webresourceRoot, $"{baseSolution.PublisherPrefix}_{solutionName}"), cancellationToken);

        await Task.WhenAll(baseResourcesTask, localResourcesTask).ConfigureAwait(false);

        // Phase 2: LCID expansion + RESX matching now that both local and Dataverse names are available.
        var dataverseNames = baseResourcesTask.Result
            .Select(e => e.GetAttributeValue<string>("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enrichedLocals = EnrichDependencies(localResourcesTask.Result, dataverseNames);

        var ownershipTasks = baseResourcesTask.Result.Select(async entity =>
        {
            var ownership = await GetOwnershipAsync(service, entity.Id, solutionName, cancellationToken).ConfigureAwait(false);
            return ToDataverseWebResource(entity, ownership);
        });

        var dataverseResources = await Task.WhenAll(ownershipTasks).ConfigureAwait(false);
        var dataverseResourcesDict = dataverseResources
            .ToDictionary(r => r.Name, r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        // Local files not in this solution may exist globally under a different solution — look them up
        // to plan AddToSolution instead of Create (Dataverse enforces global name uniqueness).
        var orphanNames = enrichedLocals.Keys
            .Where(n => !dataverseResourcesDict.ContainsKey(n))
            .ToList();

        var globalOrphans = orphanNames.Count > 0
            ? await GetGlobalWebResourcesByNameAsync(service, orphanNames, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, DataverseWebResource>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

        return new WebResourceSyncSnapshot(
            baseSolution,
            enrichedLocals,
            dataverseResourcesDict,
            globalOrphans);
    }

    IReadOnlyDictionary<string, LocalWebResource> EnrichDependencies(
        IReadOnlyDictionary<string, LocalWebResource> localResources,
        HashSet<string> dataverseNames)
    {
        var allNames = localResources.Keys
            .Concat(dataverseNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enriched = localResources.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // Phase 2a: LCID expansion — resolve bare ".resx" references (no LCID) to all matching LCID variants.
        foreach (var (name, resource) in enriched.ToList())
        {
            if (resource.Type != WebResourceType.Js) continue;
            if (resource.DependsOn.Count == 0) continue;

            var resolved = new List<string>(resource.DependsOn.Count);
            foreach (var dep in resource.DependsOn)
            {
                if (IsBareResxReference(dep))
                    resolved.AddRange(ExpandLcidVariants(dep, allNames));
                else
                    resolved.Add(dep);
            }

            if (resolved.Count != resource.DependsOn.Count || !resolved.SequenceEqual(resource.DependsOn))
                enriched[name] = resource with { DependsOn = resolved.AsReadOnly() };
        }

        // Phase 2b: RESX auto-matching — link each RESX group to its unique JS match by base name.
        var resxByBaseName = enriched.Values
            .Where(r => r.Type == WebResourceType.Resx)
            .GroupBy(r => GetResxBaseName(r.Name), StringComparer.OrdinalIgnoreCase);

        var jsByBaseName = enriched.Values
            .Where(r => r.Type == WebResourceType.Js)
            .GroupBy(r => GetJsBaseName(r.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var resxGroup in resxByBaseName)
        {
            var baseName = resxGroup.Key;
            if (!jsByBaseName.TryGetValue(baseName, out var jsMatches) || jsMatches.Count == 0)
            {
                foreach (var resx in resxGroup)
                    output.Warning($"'{resx.Name}': no JS file matches base name '{baseName}' — dependency not registered.");
                continue;
            }

            if (jsMatches.Count > 1)
            {
                foreach (var resx in resxGroup)
                    output.Warning($"'{resx.Name}': multiple JS files match base name '{baseName}' — use // flowline:depends to specify the target.");
                continue;
            }

            var jsResource = jsMatches[0];
            var added = resxGroup.Select(r => r.Name).ToList();
            enriched[jsResource.Name] = jsResource with
            {
                DependsOn = jsResource.DependsOn.Concat(added).ToList().AsReadOnly()
            };
        }

        return enriched.AsReadOnly();
    }

    static bool IsBareResxReference(string name) =>
        name.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) && !ResxLcidSuffixRegex.IsMatch(name);

    static IEnumerable<string> ExpandLcidVariants(string bareResxRef, HashSet<string> allNames)
    {
        var stem = bareResxRef[..^5]; // strip ".resx"
        return allNames.Where(n =>
            n.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) &&
            ResxLcidSuffixRegex.IsMatch(n));
    }

    static string GetResxBaseName(string logicalName)
    {
        var lastSlash = logicalName.LastIndexOf('/');
        var filename = lastSlash >= 0 ? logicalName[(lastSlash + 1)..] : logicalName;
        // filename: "MyForm.1033.resx" → strip ".resx" → "MyForm.1033" → strip LCID → "MyForm"
        filename = filename[..^5]; // strip ".resx"
        var dotIdx = filename.LastIndexOf('.');
        if (dotIdx >= 0 && filename[(dotIdx + 1)..].All(char.IsDigit))
            filename = filename[..dotIdx];
        return filename;
    }

    static string GetJsBaseName(string logicalName)
    {
        var lastSlash = logicalName.LastIndexOf('/');
        var filename = lastSlash >= 0 ? logicalName[(lastSlash + 1)..] : logicalName;
        return filename.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            ? filename[..^3]
            : filename;
    }

    async Task<IReadOnlyList<Entity>> GetWebResourcesForSolutionAsync(
        IOrganizationServiceAsync2 service, Guid solutionId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("name", "content", "displayname", "webresourcetype", "silverlightversion", "dependencyxml")
        };

        var linkComponent = query.AddLink("solutioncomponent", "webresourceid", "objectid");
        linkComponent.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
        linkComponent.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, WebResourceComponentType);

        var entities = await service.RetrieveAllAsync(query, cancellationToken).ConfigureAwait(false);
        return entities.AsReadOnly();
    }

    async Task<WebResourceOwnership> GetOwnershipAsync(
        IOrganizationServiceAsync2 service, Guid webResourceId, string currentSolutionName, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet(false),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("objectid", ConditionOperator.Equal, webResourceId),
                    new ConditionExpression("componenttype", ConditionOperator.Equal, WebResourceComponentType)
                }
            }
        };

        var solutionLink = query.AddLink("solution", "solutionid", "solutionid");
        solutionLink.Columns = new ColumnSet("uniquename", "ismanaged");
        solutionLink.EntityAlias = "solution";
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.NotEqual, DefaultSolutionUniqueName);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var solutionRefs = result.Entities
            .Select(e => new
            {
                Name = GetAliasedValue<string>(e, "solution.uniquename"),
                IsManaged = GetAliasedValue<bool>(e, "solution.ismanaged")
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToList();

        var unmanaged = solutionRefs.Where(s => s.IsManaged == false).ToList();
        var isInCurrent = unmanaged.Any(s => string.Equals(s.Name, currentSolutionName, StringComparison.OrdinalIgnoreCase));
        return new WebResourceOwnership(unmanaged.Count, isInCurrent);
    }

    static DataverseWebResource ToDataverseWebResource(Entity entity, WebResourceOwnership ownership) =>
        new(
            entity.Id,
            entity.GetAttributeValue<string>("name"),
            entity.GetAttributeValue<string>("displayname"),
            (WebResourceType)(entity.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0),
            entity.GetAttributeValue<string>("content"),
            entity,
            ownership,
            entity.GetAttributeValue<string>("dependencyxml"));

    async Task<IReadOnlyDictionary<string, DataverseWebResource>> GetGlobalWebResourcesByNameAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("name", "content", "displayname", "webresourcetype")
        };
        query.Criteria.AddCondition("name", ConditionOperator.In, names.Cast<object>().ToArray());

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities
            .Select(e => ToDataverseWebResource(e, new WebResourceOwnership(0, false)))
            .ToDictionary(r => r.Name, r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();
    }

    static IReadOnlyDictionary<string, LocalWebResource> GetLocalWebResources(string root, string prefix)
    {
        if (!Directory.Exists(root))
            return new Dictionary<string, LocalWebResource>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

        var result = new Dictionary<string, LocalWebResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace("\\", "/");
            var name = IsVerbatimPath(relativePath) ? relativePath : $"{prefix}/{relativePath}";
            if (result.TryGetValue(name, out var existing))
                throw new InvalidOperationException(
                    $"Two local files resolve to the same CRM name '{name}':\n" +
                    $"  {existing.RelativePath}\n" +
                    $"  {relativePath}");
            result[name] = LocalResourceFromFile(file, name, relativePath);
        }

        return result.AsReadOnly();
    }

    // Verbatim mode: only applies when the file is inside a subfolder whose name starts with a publisher prefix.
    // Root-level files (no '/') always use auto-prefix regardless of filename.
    static bool IsVerbatimPath(string relativePath)
    {
        var slashIndex = relativePath.IndexOf('/');
        if (slashIndex < 0) return false;
        return VerbatimPrefixRegex.IsMatch(relativePath[..slashIndex]);
    }

    static LocalWebResource LocalResourceFromFile(string path, string name, string relativePath)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (!Enum.TryParse<WebResourceType>(ext, true, out var type))
            type = WebResourceType.Unknown;

        var content = Convert.ToBase64String(File.ReadAllBytes(path));
        if (string.IsNullOrEmpty(content))
            content = null;

        // Phase 1: collect raw flowline:depends annotation lines for JS files (reads file text, not base64).
        IReadOnlyList<string> dependsOn = type == WebResourceType.Js
            ? WebResourceAnnotationParser.ParseAnnotations(path)
            : [];

        return new LocalWebResource(name, relativePath, path, Path.GetFileName(name), type, content, dependsOn);
    }

    static T? GetAliasedValue<T>(Entity entity, string attributeName)
    {
        if (!entity.Attributes.TryGetValue(attributeName, out var value))
            return default;
        return value is AliasedValue aliased ? (T?)aliased.Value : (T?)value;
    }
}
