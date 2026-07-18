using System.Text.RegularExpressions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.WebResources;

public class WebResourceReader(IAnsiConsole console)
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

        var dataverseNames = baseResourcesTask.Result
            .Select(e => e.GetAttributeValue<string>("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // dataverseResourcesDict is built before EnrichDependencies (not after, as in the original
        // ordering) because the fallback type backfill below needs it, and EnrichDependencies' LCID/RESX
        // matching needs to see the backfilled types, not the pre-backfill ones.
        var ownershipTasks = baseResourcesTask.Result.Select(async entity =>
        {
            var ownership = await GetOwnershipAsync(service, entity.Id, solutionName, cancellationToken).ConfigureAwait(false);
            return ToDataverseWebResource(entity, ownership);
        });

        var dataverseResources = await Task.WhenAll(ownershipTasks).ConfigureAwait(false);
        var dataverseResourcesDict = dataverseResources
            .ToDictionary(r => r.Name, r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        var backfilledLocals = BackfillUnresolvedTypes(localResourcesTask.Result, dataverseResourcesDict);

        var enrichedLocals = EnrichDependencies(backfilledLocals, dataverseNames);

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

    // Fallback type resolution for local files whose extension is missing or unrecognized
    // (WebResourceType.Unknown after LocalResourceFromFile). Tier 1: adopt the type of a matching
    // same-solution Dataverse resource. Files a Tier 1 match resolves to Js never got their
    // // flowline:depends annotations parsed at read time (LocalResourceFromFile only parses them
    // when the extension-derived type is already Js), so annotations are re-parsed here before
    // EnrichDependencies runs. Never overrides a type already resolved from a recognized extension.
    IReadOnlyDictionary<string, LocalWebResource> BackfillUnresolvedTypes(
        IReadOnlyDictionary<string, LocalWebResource> localResources,
        IReadOnlyDictionary<string, DataverseWebResource> dataverseResourcesDict)
    {
        Dictionary<string, LocalWebResource>? backfilled = null;

        foreach (var (name, resource) in localResources)
        {
            if (resource.Type != WebResourceType.Unknown) continue;

            WebResourceType resolvedType;
            string reason;
            string source;

            if (dataverseResourcesDict.TryGetValue(name, out var dataverseMatch))
            {
                resolvedType = dataverseMatch.Type;
                reason = "resolved type";
                source = "from the existing Dataverse record";
            }
            else
            {
                // Tier 2: no same-solution Dataverse match (including a match that exists only under
                // a different solution — R3's confirmed scope decision falls that case through to here
                // rather than a broader cross-solution lookup). Content sniffing is a guess even when
                // constrained to strong signals (KTD4), so it always warns too.
                // resource.Content is already the base64-encoded bytes LocalResourceFromFile read from
                // disk — decode that instead of reading the file a second time. Null means the file was
                // empty (LocalResourceFromFile nulls out empty content), so there's nothing to sniff.
                var sniffed = resource.Content is null
                    ? null
                    : WebResourceTypeSniffer.TrySniff(Convert.FromBase64String(resource.Content));
                if (sniffed is null) continue;

                resolvedType = sniffed.Value;
                reason = "inferred type";
                source = "from file content";
            }

            var dependsOn = resolvedType == WebResourceType.Js
                ? WebResourceAnnotationParser.ParseAnnotations(resource.Path)
                : resource.DependsOn;

            backfilled ??= localResources.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            backfilled[name] = resource with { Type = resolvedType, DependsOn = dependsOn };

            console.Warning(
                $"'{name}' has no file extension — {reason} '{resolvedType}' {source}. " +
                $"Recommend creating a properly-named replacement (e.g. '{name}.{ConventionalExtension(resolvedType)}') " +
                "and migrating references — Dataverse web resource names can't be renamed after creation.");
        }

        return backfilled?.AsReadOnly() ?? localResources;
    }

    static string ConventionalExtension(WebResourceType type) => type switch
    {
        WebResourceType.Html => "html",
        WebResourceType.Css => "css",
        WebResourceType.Js => "js",
        WebResourceType.Xml => "xml",
        WebResourceType.Png => "png",
        WebResourceType.Jpg => "jpg",
        WebResourceType.Gif => "gif",
        WebResourceType.Xap => "xap",
        WebResourceType.Xsl => "xsl",
        WebResourceType.Ico => "ico",
        WebResourceType.Svg => "svg",
        WebResourceType.Resx => "resx",
        _ => type.ToString().ToLowerInvariant()
    };

    IReadOnlyDictionary<string, LocalWebResource> EnrichDependencies(
        IReadOnlyDictionary<string, LocalWebResource> localResources,
        HashSet<string> dataverseNames)
    {
        var allNames = localResources.Keys
            .Concat(dataverseNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enriched = localResources.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        ExpandLcidDependencies(enriched, allNames);
        AutoMatchResxDependencies(enriched);

        return enriched.AsReadOnly();
    }

    // Phase 2a: resolve bare ".resx" references (no LCID) to all matching LCID variants.
    void ExpandLcidDependencies(Dictionary<string, LocalWebResource> enriched, HashSet<string> allNames)
    {
        foreach (var (name, resource) in enriched.ToList())
        {
            if (resource.Type != WebResourceType.Js) continue;
            if (resource.DependsOn.Count == 0) continue;

            var resolved = new List<string>(resource.DependsOn.Count);
            foreach (var dep in resource.DependsOn)
            {
                if (!IsBareResxReference(dep))
                {
                    resolved.Add(dep);
                    continue;
                }

                var expanded = ExpandLcidVariants(dep, allNames).ToList();
                if (expanded.Count > 0)
                    resolved.AddRange(expanded);
                else
                {
                    console.Warning($"'{name}': bare RESX reference '{dep}' has no LCID variants — kept as-is.");
                    resolved.Add(dep);
                }
            }

            if (resolved.Count != resource.DependsOn.Count || !resolved.SequenceEqual(resource.DependsOn))
                enriched[name] = resource with { DependsOn = resolved.AsReadOnly() };
        }
    }

    // Phase 2b: link each RESX group to its unique JS match by folder-qualified base name.
    void AutoMatchResxDependencies(Dictionary<string, LocalWebResource> enriched)
    {
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
                    console.Warning($"'{resx.Name}': no JS file matches base name '{baseName}' — dependency not registered.");
                continue;
            }

            if (jsMatches.Count > 1)
            {
                foreach (var resx in resxGroup)
                    console.Warning($"'{resx.Name}': multiple JS files match base name '{baseName}' — use // flowline:depends to specify the target.");
                continue;
            }

            var jsResource = jsMatches[0];
            var added = resxGroup.Select(r => r.Name).ToList();
            enriched[jsResource.Name] = jsResource with
            {
                DependsOn = jsResource.DependsOn.Concat(added).ToList().AsReadOnly()
            };
        }
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

    // Folder-qualified base name: "av_ns/MyForm.1033.resx" → "av_ns/MyForm"
    static string GetResxBaseName(string logicalName)
    {
        var stem = logicalName[..^5]; // strip ".resx"
        var lastSlash = stem.LastIndexOf('/');
        var dotIdx = stem.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx > lastSlash && stem[(dotIdx + 1)..].All(char.IsDigit))
            stem = stem[..dotIdx];
        return stem;
    }

    // Folder-qualified base name: "av_ns/MyForm.js" → "av_ns/MyForm"
    static string GetJsBaseName(string logicalName) =>
        logicalName.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            ? logicalName[..^3]
            : logicalName;

    static async Task<IReadOnlyList<Entity>> GetWebResourcesForSolutionAsync(
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

    static async Task<WebResourceOwnership> GetOwnershipAsync(
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

    static async Task<IReadOnlyDictionary<string, DataverseWebResource>> GetGlobalWebResourcesByNameAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("name", "content", "displayname", "webresourcetype", "dependencyxml")
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
