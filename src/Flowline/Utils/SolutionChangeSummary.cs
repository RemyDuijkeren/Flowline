using System.Text.RegularExpressions;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Core;
using Spectre.Console;

namespace Flowline.Utils;

public class SolutionChangeSummary
{
    public enum ChangeStatus { Added, Modified, Deleted }
    public record ChangeItem(string ComponentName, IReadOnlyList<string> FilePaths, ChangeStatus Status = ChangeStatus.Modified);
    public record ChangeGroup(string Label, IReadOnlyList<ChangeItem> Items, bool IsEntity = false);

    internal enum XmlRead { None, FormTitle, ViewTitle, DashboardName, StepName, WorkflowName }
    internal record ParsedPath(string Group, string ComponentKey, string? StaticName, XmlRead XmlRead = XmlRead.None, string? NameSuffix = null, bool IsEntity = false, string? FallbackName = null);

    public int TotalFiles { get; }
    public int LinesAdded { get; }
    public int LinesRemoved { get; }
    public IReadOnlyList<ChangeGroup> Groups { get; }

    internal SolutionChangeSummary(int totalFiles, int linesAdded, int linesRemoved, IReadOnlyList<ChangeGroup> groups)
    {
        TotalFiles = totalFiles;
        LinesAdded = linesAdded;
        LinesRemoved = linesRemoved;
        Groups = groups;
    }

    public static async Task<SolutionChangeSummary> ComputeAsync(string srcFolder, string workingDirectory, CancellationToken ct = default)
    {
        var srcRelPath = Path.GetRelativePath(workingDirectory, srcFolder).Replace('\\', '/');

        var statusResult = await Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(args => args
                .Add("-c").Add("core.quotepath=false")
                .Add("status").Add("--porcelain").Add("-uall")
                .Add("--").Add(srcRelPath))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        var changedFiles = statusResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 3)
            .Select(line => (status: line[..2], path: line[3..].Replace('\\', '/')))
            .Where(f => !IsExcluded(f.path))
            .ToList();

        if (changedFiles.Count == 0)
            return new SolutionChangeSummary(0, 0, 0, []);

        var numstatResult = await Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(args => args
                .Add("-c").Add("core.quotepath=false")
                .Add("diff").Add("HEAD").Add("--numstat")
                .Add("--").Add(srcRelPath))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        var linesByPath = numstatResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(p => p.Length >= 3 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .ToDictionary(p => p[2], p => (added: int.Parse(p[0]), removed: int.Parse(p[1])));

        var srcPrefix = srcRelPath.TrimEnd('/') + "/";
        var components = new Dictionary<string, (ParsedPath Parsed, List<string> Paths, List<string> FileStatuses)>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;
        int totalAdded = 0, totalRemoved = 0;

        foreach (var (status, absPath) in changedFiles)
        {
            var relPath = absPath.StartsWith(srcPrefix) ? absPath[srcPrefix.Length..] : absPath;
            var parsed = ParseComponentPath(relPath);
            if (parsed == null) continue;

            fileCount++;

            if (status.Trim() == "??")
            {
                var fullPath = Path.Combine(workingDirectory, absPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                    totalAdded += CountLines(fullPath);
            }
            else if (linesByPath.TryGetValue(absPath, out var counts))
            {
                totalAdded += counts.added;
                totalRemoved += counts.removed;
            }

            if (!components.TryGetValue(parsed.ComponentKey, out var comp))
            {
                comp = (parsed, [], []);
                components[parsed.ComponentKey] = comp;
            }
            comp.Paths.Add(relPath);
            comp.FileStatuses.Add(status);
        }

        if (fileCount == 0)
            return new SolutionChangeSummary(0, 0, 0, []);

        var resolvedGroups = new List<ChangeGroup>();
        foreach (var g in components.Values.GroupBy(c => c.Parsed.Group))
        {
            var items = new List<ChangeItem>();
            foreach (var c in g)
            {
                var name = c.Parsed.StaticName ?? await ResolveXmlNameAsync(
                    c.Paths[0], srcFolder, workingDirectory, srcRelPath, c.Parsed, ct);
                items.Add(new ChangeItem(name, c.Paths, AggregateStatus(c.FileStatuses)));
            }
            resolvedGroups.Add(new ChangeGroup(g.Key, items, g.First().Parsed.IsEntity));
        }

        return new SolutionChangeSummary(fileCount, totalAdded, totalRemoved, resolvedGroups);
    }

    public void Write(IAnsiConsole console, string? envName, bool verbose)
    {
        if (TotalFiles == 0)
        {
            console.Info($"No changes pulled from {Markup.Escape(envName ?? "DEV")}.");
            return;
        }

        var headline = $"Changes ({TotalFiles} {(TotalFiles == 1 ? "file" : "files")}, +{LinesAdded} -{LinesRemoved})";
        var tree = new Tree(headline);

        var entityGroups = Groups.Where(g => g.IsEntity).ToList();
        var otherGroups = Groups.Where(g => !g.IsEntity).ToList();

        if (entityGroups.Count > 0)
        {
            var entitiesNode = tree.AddNode("Entities");
            foreach (var group in entityGroups)
                AddGroupItems(entitiesNode.AddNode(Markup.Escape(group.Label)), group, verbose);
        }

        foreach (var group in otherGroups)
            AddGroupItems(tree.AddNode(Markup.Escape(group.Label)), group, verbose);

        console.Write(tree);
    }

    internal static ParsedPath? ParseComponentPath(string pathRelativeToSrc)
    {
        var path = pathRelativeToSrc.Replace('\\', '/');

        if (path.StartsWith("Other/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("Other", StringComparison.OrdinalIgnoreCase))
            return null;

        if (path.StartsWith("Entities/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/');
            if (parts.Length < 3) return null;
            var entity = parts[1];

            if (parts.Length == 3)
            {
                if (parts[2].Equals("Entity.xml", StringComparison.OrdinalIgnoreCase))
                    return new ParsedPath(entity, entity + "/entity", "entity metadata", IsEntity: true);
                if (parts[2].Equals("RibbonDiff.xml", StringComparison.OrdinalIgnoreCase))
                    return new ParsedPath(entity, entity + "/ribbon", "ribbon", IsEntity: true);
            }

            if (parts[2].Equals("FormXml", StringComparison.OrdinalIgnoreCase) && parts.Length >= 5)
            {
                var formType = parts[3];
                var guid = Path.GetFileNameWithoutExtension(parts[4]);
                return new ParsedPath(entity, entity + "/form/" + guid, null, XmlRead.FormTitle, formType + " form", IsEntity: true);
            }

            if (parts[2].Equals("SavedQueries", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
            {
                var guid = Path.GetFileNameWithoutExtension(parts[3]);
                return new ParsedPath(entity, entity + "/view/" + guid, null, XmlRead.ViewTitle, "view", IsEntity: true);
            }

            if (parts[2].Equals("Formulas", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
            {
                var name = Path.GetFileNameWithoutExtension(parts[3]);
                return new ParsedPath(entity, entity + "/formula/" + name, $"formula: {name}", IsEntity: true);
            }

            return null;
        }

        var slash = path.IndexOf('/');
        var top = slash >= 0 ? path[..slash] : path;
        var rest = slash >= 0 ? path[(slash + 1)..] : string.Empty;

        if (top.Equals("Workflows", StringComparison.OrdinalIgnoreCase))
        {
            var isJsonDataXml = rest.EndsWith(".json.data.xml", StringComparison.OrdinalIgnoreCase);
            var effectiveName = rest.EndsWith(".data.xml", StringComparison.OrdinalIgnoreCase)
                ? rest[..^".data.xml".Length]
                : rest;
            var stem = Path.GetFileNameWithoutExtension(effectiveName);
            var stripped = StripGuidSuffix(stem);
            return isJsonDataXml
                ? new ParsedPath("Workflows", "Workflows/" + stem, null, XmlRead.WorkflowName, FallbackName: stripped)
                : new ParsedPath("Workflows", "Workflows/" + stem, stripped);
        }

        if (top.Equals("OptionSets", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(rest);
            return new ParsedPath("OptionSets", "OptionSets/" + name, name);
        }

        if (top.Equals("Roles", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(rest);
            return new ParsedPath("Roles", "Roles/" + name, name);
        }

        if (top.Equals("environmentvariabledefinitions", StringComparison.OrdinalIgnoreCase))
        {
            var varName = rest.Split('/')[0];
            return new ParsedPath("Environment Variables", "envvars/" + varName, varName);
        }

        if (top.Equals("SdkMessageProcessingSteps", StringComparison.OrdinalIgnoreCase))
        {
            var guid = Path.GetFileNameWithoutExtension(rest);
            return new ParsedPath("Plugin Steps", "pluginsteps/" + guid, null, XmlRead.StepName);
        }

        if (top.Equals("Dashboards", StringComparison.OrdinalIgnoreCase))
        {
            var guid = Path.GetFileNameWithoutExtension(rest);
            return new ParsedPath("Dashboards", "Dashboards/" + guid, null, XmlRead.DashboardName);
        }

        if (top.Equals("AppModules", StringComparison.OrdinalIgnoreCase) ||
            top.Equals("AppModuleSiteMaps", StringComparison.OrdinalIgnoreCase))
        {
            var appName = rest.Split('/')[0];
            return new ParsedPath("App Modules", "AppModules/" + appName, appName);
        }

        if (top.Equals("WebResources", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(rest)) return null;
            var effectivePath = rest.EndsWith(".data.xml", StringComparison.OrdinalIgnoreCase)
                ? rest[..^".data.xml".Length]
                : rest;
            return new ParsedPath("Web Resources", "WebResources/" + effectivePath, effectivePath);
        }

        return null;
    }

    static void AddGroupItems(TreeNode groupNode, ChangeGroup group, bool verbose)
    {
        if (group.Label == "Web Resources")
        {
            AddFileTreeNodes(groupNode, group.Items, verbose);
            return;
        }
        foreach (var item in group.Items)
        {
            var itemNode = groupNode.AddNode($"{StatusIcon(item.Status)} {Markup.Escape(item.ComponentName)}");
            if (verbose)
                foreach (var path in item.FilePaths)
                    itemNode.AddNode($"[dim]{Markup.Escape(path)}[/]");
        }
    }

    static string StatusIcon(ChangeStatus status) => status switch
    {
        ChangeStatus.Added   => "[green]+[/]",
        ChangeStatus.Deleted => "[red]-[/]",
        _                    => "[yellow]~[/]"
    };

    static void AddFileTreeNodes(IHasTreeNodes parent, IEnumerable<ChangeItem> items, bool verbose)
    {
        var bySegment = items.GroupBy(item => {
            var idx = item.ComponentName.IndexOf('/');
            return idx >= 0 ? item.ComponentName[..idx] : string.Empty;
        });

        foreach (var g in bySegment)
        {
            if (g.Key == string.Empty)
            {
                foreach (var item in g)
                {
                    var leaf = parent.AddNode($"{StatusIcon(item.Status)} {Markup.Escape(item.ComponentName)}");
                    if (verbose)
                        foreach (var path in item.FilePaths)
                            leaf.AddNode($"[dim]{Markup.Escape(path)}[/]");
                }
            }
            else
            {
                var folderNode = parent.AddNode(Markup.Escape(g.Key));
                var subItems = g.Select(item => {
                    var idx = item.ComponentName.IndexOf('/');
                    return item with { ComponentName = item.ComponentName[(idx + 1)..] };
                });
                AddFileTreeNodes(folderNode, subItems, verbose);
            }
        }
    }

    static ChangeStatus ClassifyFileStatus(string gitStatus) =>
        gitStatus == "??" || gitStatus[0] == 'A' || gitStatus[1] == 'A' ? ChangeStatus.Added :
        gitStatus[0] == 'D' || gitStatus[1] == 'D' ? ChangeStatus.Deleted :
        ChangeStatus.Modified;

    static ChangeStatus AggregateStatus(IEnumerable<string> gitStatuses)
    {
        var classified = gitStatuses.Select(ClassifyFileStatus).Distinct().ToList();
        return classified.Count == 1 ? classified[0] : ChangeStatus.Modified;
    }

    internal static bool IsExcluded(string path) =>
        path.EndsWith("_managed.xml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".xaml.data.xml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dll.data.xml", StringComparison.OrdinalIgnoreCase);

    static string StripGuidSuffix(string name)
    {
        var m = Regex.Match(name, @"-[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$");
        return m.Success ? name[..m.Index] : name;
    }

    static int CountLines(string filePath)
    {
        try { return File.ReadAllLines(filePath).Length; }
        catch { return 0; }
    }

    static async Task<string> ResolveXmlNameAsync(
        string relPath, string srcFolder, string workingDirectory, string srcRelPath,
        ParsedPath parsed, CancellationToken ct)
    {
        var fullPath = Path.Combine(srcFolder, relPath.Replace('/', Path.DirectorySeparatorChar));
        string? xml = null;

        if (File.Exists(fullPath))
        {
            try { xml = await File.ReadAllTextAsync(fullPath, ct); }
            catch { }
        }
        else
        {
            var gitPath = srcRelPath.TrimEnd('/') + "/" + relPath;
            var result = await Cli.Wrap("git")
                .WithWorkingDirectory(workingDirectory)
                .WithArguments(args => args.Add("show").Add($"HEAD:{gitPath}"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
            if (result.ExitCode == 0)
                xml = result.StandardOutput;
        }

        var fallback = parsed.FallbackName ?? Path.GetFileNameWithoutExtension(relPath);
        return ResolveXmlName(xml, parsed.XmlRead, parsed.NameSuffix) ?? fallback;
    }

    static string? ResolveXmlName(string? xml, XmlRead xmlRead, string? nameSuffix)
    {
        if (xml == null || xmlRead == XmlRead.None) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            var title = xmlRead switch
            {
                XmlRead.StepName or XmlRead.WorkflowName => (string?)doc.Root?.Attribute("Name"),
                _ => GetLocalizedName(doc)
            };
            return nameSuffix != null && title != null ? $"{title} ({nameSuffix})" : title;
        }
        catch { return null; }
    }

    static string? GetLocalizedName(XDocument doc)
    {
        var ln = doc.Descendants("LocalizedName")
            .FirstOrDefault(e => (string?)e.Attribute("languagecode") == "1033")
            ?? doc.Descendants("LocalizedName").FirstOrDefault();
        return (string?)ln?.Attribute("description");
    }
}
