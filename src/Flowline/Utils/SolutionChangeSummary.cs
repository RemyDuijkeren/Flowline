using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Diagnostics;
using Spectre.Console;

namespace Flowline.Utils;

public class SolutionChangeSummary
{
    public enum ChangeStatus { Added, Modified, Deleted }
    public record SubChange(string Description, ChangeStatus Status);
    public record ChangeItem(string ComponentName, IReadOnlyList<string> FilePaths, ChangeStatus Status = ChangeStatus.Modified, IReadOnlyList<SubChange>? SubChanges = null);

    internal static int SubChangeDisplayThreshold = 5;
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

    public static async Task<SolutionChangeSummary> ComputeAsync(string srcFolder, string workingDirectory, SubprocessCapture? capture = null, CancellationToken ct = default)
    {
        var srcRelPath = Path.GetRelativePath(workingDirectory, srcFolder).Replace('\\', '/');

        static Task<CliWrap.Buffered.BufferedCommandResult> Run(Command cmd, SubprocessCapture? cap, CancellationToken ct, bool suppressErrors = false) =>
            (cap?.Apply(cmd, suppressErrors: suppressErrors) ?? cmd).ExecuteBufferedAsync(ct);

        var statusResult = await Run(
            Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(args => args
                .Add("-c").Add("core.quotepath=false")
                .Add("-c").Add("core.safecrlf=false")
                .Add("status").Add("--porcelain").Add("-uall")
                .Add("--").Add(srcRelPath))
            .WithValidation(CommandResultValidation.None),
            capture, ct);

        var changedFiles = statusResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 3)
            .Select(line => (status: line[..2], path: line[3..].Replace('\\', '/')))
            .Where(f => !IsExcluded(f.path))
            .ToList();

        if (changedFiles.Count == 0)
            return new SolutionChangeSummary(0, 0, 0, []);

        var numstatResult = await Run(
            Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(args => args
                .Add("-c").Add("core.quotepath=false")
                .Add("-c").Add("core.safecrlf=false")
                .Add("diff").Add("HEAD").Add("--numstat")
                .Add("--").Add(srcRelPath))
            .WithValidation(CommandResultValidation.None),
            capture, ct, suppressErrors: true);

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
                var status = AggregateStatus(c.FileStatuses);
                var subChanges = await ResolveSubChangesAsync(c.Parsed, c.Paths[0], srcFolder, workingDirectory, srcRelPath, status, ct);
                items.Add(new ChangeItem(name, c.Paths, status, subChanges));
            }
            resolvedGroups.Add(new ChangeGroup(g.Key, items, g.First().Parsed.IsEntity));
        }

        return new SolutionChangeSummary(fileCount, totalAdded, totalRemoved, resolvedGroups);
    }

    public async Task WriteChangesFileAsync(string slnFolder, string solutionName, string? envName, CancellationToken ct = default)
    {
        if (TotalFiles == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Changes — {solutionName} ({DateTime.Now:yyyy-MM-dd})");
        sb.AppendLine();
        if (envName != null)
        {
            sb.AppendLine($"Synced from: {envName}");
            sb.AppendLine();
        }

        static string Icon(ChangeStatus s) => s switch
        {
            ChangeStatus.Added   => "`+`",
            ChangeStatus.Deleted => "`-`",
            _                    => "`~`"
        };

        static void WriteSubChanges(System.Text.StringBuilder sb, IReadOnlyList<SubChange> subs)
        {
            foreach (var sub in subs)
                sb.AppendLine($"- {Icon(sub.Status)} {sub.Description}");
        }

        var entityGroups = Groups.Where(g => g.IsEntity).OrderBy(g => g.Label).ToList();
        var optionSetGroups = Groups.Where(g => g.Label == "OptionSets").ToList();
        var otherGroups = Groups.Where(g => !g.IsEntity && g.Label != "OptionSets").OrderBy(g => g.Label).ToList();

        if (entityGroups.Count > 0)
        {
            sb.AppendLine("## Entities");
            sb.AppendLine();
            foreach (var group in entityGroups)
            {
                sb.AppendLine($"### {group.Label}");
                foreach (var item in group.Items.OrderBy(i => i.ComponentName))
                {
                    if (item.SubChanges is { Count: > 0 })
                    {
                        sb.AppendLine();
                        sb.AppendLine($"**{item.ComponentName}**");
                        WriteSubChanges(sb, item.SubChanges);
                    }
                    else
                        sb.AppendLine($"- {Icon(item.Status)} {item.ComponentName}");
                }
                sb.AppendLine();
            }
        }

        foreach (var group in optionSetGroups)
        {
            sb.AppendLine("## OptionSets");
            sb.AppendLine();
            foreach (var item in group.Items.OrderBy(i => i.ComponentName))
            {
                sb.AppendLine($"### {item.ComponentName}");
                if (item.SubChanges is { Count: > 0 })
                    WriteSubChanges(sb, item.SubChanges);
                else
                    sb.AppendLine($"- {Icon(item.Status)} {item.ComponentName}");
                sb.AppendLine();
            }
        }

        foreach (var group in otherGroups)
        {
            sb.AppendLine($"## {group.Label}");
            sb.AppendLine();
            foreach (var item in group.Items.OrderBy(i => i.ComponentName))
                sb.AppendLine($"- {Icon(item.Status)} {item.ComponentName}");
            sb.AppendLine();
        }

        var outputPath = Path.Combine(slnFolder, "CHANGES.md");
        Directory.CreateDirectory(slnFolder);
        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct);
    }

    public void WriteFlat(IAnsiConsole console, FlowlineRuntimeOptions options, string? markupPrefix = null)
    {
        foreach (var group in Groups.OrderBy(g => g.IsEntity ? 0 : 1).ThenBy(g => g.Label))
        {
            foreach (var item in group.Items.OrderBy(i => i.ComponentName))
            {
                var line = $"- {StatusIcon(item.Status)} {(group.IsEntity ? "Entity " : "")}{group.Label}: {Markup.Escape(item.ComponentName)}";
                if (markupPrefix is not null)
                    console.MarkupLine($"{markupPrefix}{line}[/]");
                else
                    console.Info(line);
                foreach (var path in item.FilePaths)
                    console.Verbose($"    {Markup.Escape(path)}");
            }
        }
    }

    public void WriteTree(IAnsiConsole console, string? envName, bool verbose)
    {
        if (TotalFiles == 0)
        {
            console.Info($"No changes pulled from {Markup.Escape(envName ?? "DEV")}.");
            return;
        }

        var headline = $"\nChanges ({TotalFiles} {(TotalFiles == 1 ? "file" : "files")}, +{LinesAdded} -{LinesRemoved})";
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
            var label = $"{StatusIcon(item.Status)} {Markup.Escape(item.ComponentName)}";

            if (SubChangeDisplayThreshold == 0 && item.SubChanges is { Count: > 0 })
            {
                var added    = item.SubChanges.Count(s => s.Status == ChangeStatus.Added);
                var removed  = item.SubChanges.Count(s => s.Status == ChangeStatus.Deleted);
                var modified = item.SubChanges.Count(s => s.Status == ChangeStatus.Modified);
                var parts    = new List<string>();
                if (added   > 0) parts.Add($"{added} added");
                if (removed > 0) parts.Add($"{removed} removed");
                if (modified > 0) parts.Add($"{modified} modified");
                label += $" ({string.Join(", ", parts)})";
            }

            var itemNode = groupNode.AddNode(label);

            if (SubChangeDisplayThreshold > 0 && item.SubChanges is { Count: > 0 })
            {
                var shown = item.SubChanges.Take(SubChangeDisplayThreshold).ToList();
                foreach (var sub in shown)
                    itemNode.AddNode($"{StatusIcon(sub.Status)} {Markup.Escape(sub.Description)}");
                var overflow = item.SubChanges.Count - shown.Count;
                if (overflow > 0)
                    itemNode.AddNode($"[dim]...and {overflow} more (see CHANGES.md)[/]");
            }

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
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        else
        {
            var gitPath = srcRelPath.TrimEnd('/') + "/" + relPath;
            var logResult = await Cli.Wrap("git")
                .WithWorkingDirectory(workingDirectory)
                .WithArguments(args => args.Add("log").Add("-n1").Add("--format=%H").Add("--").Add(gitPath))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
            var commitHash = logResult.StandardOutput.Trim();
            if (!string.IsNullOrEmpty(commitHash))
            {
                var showResult = await Cli.Wrap("git")
                    .WithWorkingDirectory(workingDirectory)
                    .WithArguments(args => args.Add("show").Add($"{commitHash}:{gitPath}"))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(ct);
                if (showResult.ExitCode == 0)
                    xml = showResult.StandardOutput;
            }
        }

        var fallback = parsed.FallbackName ?? Path.GetFileNameWithoutExtension(relPath);
        return ResolveXmlName(xml, parsed.XmlRead, parsed.NameSuffix) ?? fallback;
    }

    static string? ResolveXmlName(string? xml, XmlRead xmlRead, string? nameSuffix)
    {
        if (xml == null || xmlRead == XmlRead.None) return null;
        try
        {
            // git show preserves the UTF-8 BOM; XDocument.Parse rejects a leading BOM
            var doc = XmlHelpers.Parse(xml);
            var title = xmlRead switch
            {
                XmlRead.StepName or XmlRead.WorkflowName => (string?)doc.Root?.Attribute("Name"),
                _ => GetLocalizedName(doc)
            };
            return nameSuffix != null && title != null ? $"{title} ({nameSuffix})" : title;
        }
        catch (XmlException) { return null; }
    }

    static async Task<IReadOnlyList<SubChange>?> ResolveSubChangesAsync(
        ParsedPath parsed, string relPath, string srcFolder, string workingDirectory,
        string srcRelPath, ChangeStatus status, CancellationToken ct)
    {
        bool isEntityMeta = parsed.ComponentKey.EndsWith("/entity", StringComparison.OrdinalIgnoreCase);
        bool isView       = parsed.XmlRead == XmlRead.ViewTitle;
        bool isForm       = parsed.XmlRead == XmlRead.FormTitle;
        bool isOptionSet  = string.Equals(parsed.Group, "OptionSets", StringComparison.OrdinalIgnoreCase);

        if (!isEntityMeta && !isView && !isForm && !isOptionSet) return null;
        if (status == ChangeStatus.Added) return null;

        var oldXml = await GetHeadXmlAsync(relPath, srcRelPath, workingDirectory, ct);
        var newXml = status == ChangeStatus.Deleted ? null : await GetCurrentXmlAsync(relPath, srcFolder, ct);

        if (oldXml == null && newXml == null) return null;

        var result = isEntityMeta ? DiffEntityAttributes(oldXml, newXml)
                   : isView       ? DiffSavedQuery(oldXml, newXml)
                   : isForm       ? DiffFormXml(oldXml, newXml)
                   :                DiffOptionSet(oldXml, newXml);

        return result is { Count: > 0 } ? result : null;
    }

    static async Task<string?> GetHeadXmlAsync(string relPath, string srcRelPath, string workingDirectory, CancellationToken ct)
    {
        var gitPath = srcRelPath.TrimEnd('/') + "/" + relPath;
        var result = await Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(args => args
                .Add("-c").Add("core.quotepath=false")
                .Add("show").Add($"HEAD:{gitPath}"))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        return result.ExitCode == 0 ? XmlHelpers.StripBom(result.StandardOutput) : null;
    }

    static async Task<string?> GetCurrentXmlAsync(string relPath, string srcFolder, CancellationToken ct)
    {
        var fullPath = Path.Combine(srcFolder, relPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return null;
        try { return await File.ReadAllTextAsync(fullPath, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static List<SubChange>? DiffEntityAttributes(string? oldXml, string? newXml)
    {
        var oldAttribs = ParseXmlElements(oldXml, "attribute", e => (string?)e.Element("LogicalName"));
        var newAttribs = ParseXmlElements(newXml, "attribute", e => (string?)e.Element("LogicalName"));
        if (oldAttribs == null && newAttribs == null) return null;

        var added    = new List<SubChange>();
        var removed  = new List<SubChange>();
        var modified = new List<SubChange>();

        foreach (var (name, el) in newAttribs ?? [])
        {
            if (!(oldAttribs?.ContainsKey(name) ?? false))
            {
                var type = el.Element("Type")?.Value;
                added.Add(new SubChange(type != null ? $"{name} ({type})" : name, ChangeStatus.Added));
            }
            else if (oldAttribs.TryGetValue(name, out var oldEl) && oldEl.ToString() != el.ToString())
                modified.Add(new SubChange(name, ChangeStatus.Modified));
        }
        foreach (var name in (oldAttribs ?? []).Keys.Where(k => !(newAttribs?.ContainsKey(k) ?? false)))
            removed.Add(new SubChange(name, ChangeStatus.Deleted));

        return [..added, ..removed, ..modified];
    }

    static List<SubChange>? DiffSavedQuery(string? oldXml, string? newXml)
    {
        static HashSet<string> Cols(string? xml) =>
            ParseXmlElements(xml, "cell", e => (string?)e.Attribute("name"))?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];

        XDocument? ParseDoc(string? xml) { try { return xml != null ? XmlHelpers.Parse(xml) : null; } catch { return null; } }

        var oldDoc = ParseDoc(oldXml);
        var newDoc = ParseDoc(newXml);

        var oldCols = Cols(oldXml);
        var newCols = Cols(newXml);

        var added   = new List<SubChange>();
        var removed = new List<SubChange>();
        var flags   = new List<SubChange>();

        foreach (var col in newCols.Where(c => !oldCols.Contains(c)))
            added.Add(new SubChange(col, ChangeStatus.Added));
        foreach (var col in oldCols.Where(c => !newCols.Contains(c)))
            removed.Add(new SubChange(col, ChangeStatus.Deleted));

        // Filter: compare fetchxml excluding order and attribute elements (attributes mirror layout columns)
        var oldFetch = oldDoc?.Descendants("fetchxml").FirstOrDefault();
        var newFetch = newDoc?.Descendants("fetchxml").FirstOrDefault();
        if (oldFetch != null || newFetch != null)
        {
            static string StripColumnsAndOrders(XElement? el)
            {
                if (el == null) return string.Empty;
                var clone = new XElement(el);
                clone.Descendants("order").Remove();
                clone.Descendants("attribute").Remove();
                return clone.ToString(SaveOptions.DisableFormatting);
            }
            if (StripColumnsAndOrders(oldFetch) != StripColumnsAndOrders(newFetch))
                flags.Add(new SubChange("filter changed", ChangeStatus.Modified));

            static string OrdersOnly(XElement? el) =>
                string.Concat(el?.Descendants("order").Select(o => o.ToString(SaveOptions.DisableFormatting)) ?? []);
            if (OrdersOnly(oldFetch) != OrdersOnly(newFetch))
                flags.Add(new SubChange("sort changed", ChangeStatus.Modified));
        }

        return [..added, ..removed, ..flags];
    }

    static List<SubChange>? DiffOptionSet(string? oldXml, string? newXml)
    {
        static string? GetLabel(XElement el) =>
            (string?)el.Descendants("label")
                .FirstOrDefault(l => (string?)l.Attribute("languagecode") == "1033")
                ?.Attribute("description")
            ?? (string?)el.Descendants("label").FirstOrDefault()?.Attribute("description");

        var oldOpts = ParseXmlElements(oldXml, "option", e => (string?)e.Attribute("value"));
        var newOpts = ParseXmlElements(newXml, "option", e => (string?)e.Attribute("value"));
        if (oldOpts == null && newOpts == null) return null;

        var added   = new List<SubChange>();
        var removed = new List<SubChange>();
        var modified = new List<SubChange>();

        foreach (var (value, el) in newOpts ?? [])
        {
            var label = GetLabel(el) ?? value;
            if (!(oldOpts?.ContainsKey(value) ?? false))
                added.Add(new SubChange($"{label} ({value})", ChangeStatus.Added));
            else if (oldOpts.TryGetValue(value, out var oldEl) && GetLabel(oldEl) != label)
                modified.Add(new SubChange($"{label} ({value})", ChangeStatus.Modified));
        }
        foreach (var (value, el) in (oldOpts ?? []).Where(kv => !(newOpts?.ContainsKey(kv.Key) ?? false)))
            removed.Add(new SubChange($"{GetLabel(el) ?? value} ({value})", ChangeStatus.Deleted));

        return [..added, ..removed, ..modified];
    }

    static List<SubChange>? DiffFormXml(string? oldXml, string? newXml)
    {
        static string? ResolveLabel(XElement el) =>
            (string?)el.Element("labels")?.Elements("label")
                .FirstOrDefault(l => (string?)l.Attribute("languagecode") == "1033")
                ?.Attribute("description")
            ?? (string?)el.Element("labels")?.Elements("label").FirstOrDefault()?.Attribute("description")
            ?? (string?)el.Attribute("name");

        var oldFields   = ParseXmlElements(oldXml, "cell", e => (string?)e.Attribute("datafieldname"), skipEmpty: true);
        var newFields   = ParseXmlElements(newXml, "cell", e => (string?)e.Attribute("datafieldname"), skipEmpty: true);
        var oldSections = ParseXmlElements(oldXml, "section", e => (string?)e.Attribute("name"));
        var newSections = ParseXmlElements(newXml, "section", e => (string?)e.Attribute("name"));
        var oldTabs     = ParseXmlElements(oldXml, "tab", e => (string?)e.Attribute("name"));
        var newTabs     = ParseXmlElements(newXml, "tab", e => (string?)e.Attribute("name"));

        if (oldFields == null && newFields == null && oldSections == null && newSections == null
            && oldTabs == null && newTabs == null) return null;

        var added   = new List<SubChange>();
        var removed = new List<SubChange>();

        void DiffElements(Dictionary<string, XElement>? oldMap, Dictionary<string, XElement>? newMap, Func<XElement, string> label, string? prefix = null)
        {
            foreach (var (key, el) in newMap ?? [])
                if (!(oldMap?.ContainsKey(key) ?? false))
                    added.Add(new SubChange(prefix != null ? $"{prefix}: {label(el)}" : label(el), ChangeStatus.Added));
            foreach (var (key, el) in oldMap ?? [])
                if (!(newMap?.ContainsKey(key) ?? false))
                    removed.Add(new SubChange(prefix != null ? $"{prefix}: {label(el)}" : label(el), ChangeStatus.Deleted));
        }

        // Fields appearing in multiple sections deduplicate by first-seen — moving a field between sections produces no sub-change.
        DiffElements(oldFields, newFields, el => (string?)el.Attribute("datafieldname") ?? string.Empty);
        DiffElements(oldTabs, newTabs, el => ResolveLabel(el) ?? string.Empty, "tab");
        DiffElements(oldSections, newSections, el => ResolveLabel(el) ?? string.Empty, "section");

        return [..added, ..removed];
    }

    static Dictionary<string, XElement>? ParseXmlElements(string? xml, string elementName,
        Func<XElement, string?> keySelector, bool skipEmpty = false)
    {
        if (xml == null) return null;
        try
        {
            var doc = XmlHelpers.Parse(xml);
            return doc.Descendants(elementName)
                .Select(e => (key: keySelector(e), el: e))
                .Where(t => !string.IsNullOrEmpty(t.key))
                .GroupBy(t => t.key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().el, StringComparer.OrdinalIgnoreCase);
        }
        catch (XmlException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    static string? GetLocalizedName(XDocument doc)
    {
        var ln = XmlHelpers.PreferLanguage(doc.Descendants("LocalizedName"));
        return (string?)ln?.Attribute("description");
    }
}
