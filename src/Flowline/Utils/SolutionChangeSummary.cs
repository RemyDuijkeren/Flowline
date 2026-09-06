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

    /// <summary>One side of a comparison: a git ref, or the working tree when <see cref="GitRef"/> is null.</summary>
    public readonly record struct ComparisonSide(string? GitRef)
    {
        public static ComparisonSide WorkingTree => new((string?)null);
        public static ComparisonSide Head => new("HEAD");
        public bool IsWorkingTree => GitRef is null;
    }

    internal const int SubChangeDisplayThreshold = 5;
    public record ChangeGroup(string Label, IReadOnlyList<ChangeItem> Items, bool IsEntity = false);

    /// <summary>The solution version on each side, present only when the two sides differ.</summary>
    public record VersionTransition(string From, string To);

    internal enum XmlRead { None, FormTitle, ViewTitle, DashboardName, StepName, WorkflowName }
    internal record ParsedPath(string Group, string ComponentKey, string? StaticName, XmlRead XmlRead = XmlRead.None, string? NameSuffix = null, bool IsEntity = false, string? FallbackName = null);

    public int TotalFiles { get; }
    public int LinesAdded { get; }
    public int LinesRemoved { get; }
    public IReadOnlyList<ChangeGroup> Groups { get; }
    public VersionTransition? Version { get; }

    internal SolutionChangeSummary(int totalFiles, int linesAdded, int linesRemoved, IReadOnlyList<ChangeGroup> groups,
        VersionTransition? version = null)
    {
        TotalFiles = totalFiles;
        LinesAdded = linesAdded;
        LinesRemoved = linesRemoved;
        Groups = groups;
        Version = version;
    }

    public static Task<SolutionChangeSummary> ComputeAsync(string srcFolder, string workingDirectory, SubprocessCapture? capture = null, CancellationToken ct = default) =>
        ComputeAsync(srcFolder, workingDirectory, ComparisonSide.Head, ComparisonSide.WorkingTree, capture, ct);

    public static async Task<SolutionChangeSummary> ComputeAsync(string srcFolder, string workingDirectory, ComparisonSide from, ComparisonSide to, SubprocessCapture? capture = null, CancellationToken ct = default)
    {
        // The left side is always a ref: git can compare a ref against the working tree but not the other
        // way round, and every mode the CLI exposes moves only the left side. Guarded rather than left to
        // NullReferenceException inside the argument builders, because both sides are public API.
        if (from.IsWorkingTree)
            throw new ArgumentException("The working tree can only be the right-hand side of a comparison; pass a git ref as 'from'.", nameof(from));

        // Every git listing prints paths relative to the repository root, and `git show <ref>:<path>`
        // resolves from the root too — so the whole comparison runs from there and uses one path form.
        // Running from the root also neutralizes a user's diff.relative=true, which would otherwise
        // reintroduce the prefix mismatch this replaced.
        var repoRoot = GitUtils.FindRepositoryRoot(workingDirectory) ?? workingDirectory;
        // srcRelPath is handed to git as a pathspec, and '[', ']' and '*' are pathspec magic while being legal
        // in a Windows path. Every git call below that takes it passes --literal-pathspecs so a solution folder
        // containing one is matched by name instead of silently matching nothing and reporting "no changes".
        var srcRelPath = Path.GetRelativePath(repoRoot, srcFolder).Replace('\\', '/');

        // Closes over the four values every fetch needs, so a call site states only which side and
        // which file. They are fixed for the whole comparison, and same-typed strings in a parameter
        // list is a transposition waiting to happen.
        Task<SideXmlResult> SideXml(ComparisonSide side, string relPath) =>
            GetSideXmlAsync(side, relPath, srcFolder, srcRelPath, repoRoot, capture, ct);

        // --no-renames matches the diff listings below: git status otherwise emits a staged rename as
        // "R  old -> new" on one line, which the path parser reads as a single nonexistent file.
        Command Porcelain() => Cli.Wrap("git")
            .WithWorkingDirectory(repoRoot)
            .WithArguments(args => args
                .Add("-c").Add("core.quotepath=false")
                .Add("-c").Add("core.safecrlf=false")
                .Add("--literal-pathspecs")
                .Add("status").Add("--porcelain").Add("--no-renames").Add("-uall")
                .Add("--").Add(srcRelPath))
            .WithValidation(CommandResultValidation.None);

        // A listing that failed produces empty stdout, which parses as "nothing changed". Callers gate
        // CI checks and destructive overwrites on that answer, so a broken listing has to stop the run.
        void AssertListed(BufferedCommandResult result, string what)
        {
            if (result.ExitCode == 0) return;
            var stderr = result.StandardError.Trim();
            throw new FlowlineException(ExitCode.Inconclusive,
                $"Git couldn't list the changes in '{srcRelPath}' ({what}): {stderr}. "
                + $"Run 'git status' in {repoRoot} to repair the repository, then try again.");
        }

        // Do not fold this branch into the git diff path below: the diff path resolves both refs, and in a
        // repository with no commits there is no HEAD to resolve, so `git diff --name-status HEAD` fails and
        // the first sync in a fresh repo reports nothing. git status --porcelain needs no commit, so
        // HEAD-vs-working-tree stays on it. Every other pair of sides is between two refs, where only a diff
        // means anything.
        var defaultMode = to.IsWorkingTree && from.GitRef == "HEAD";
        List<ChangedFile> changedFiles;

        if (defaultMode)
        {
            var statusResult = await Run(Porcelain(), capture, ct);
            AssertListed(statusResult, "git status --porcelain");
            changedFiles = ParsePorcelain(statusResult.StandardOutput);
        }
        else
        {
            foreach (var side in new[] { from, to })
                if (side.GitRef is { } gitRef)
                    await EnsureRefExistsAsync(gitRef, repoRoot, capture, ct);

            var nameStatusResult = await Run(
                Cli.Wrap("git")
                .WithWorkingDirectory(repoRoot)
                .WithArguments(args =>
                {
                    args.Add("-c").Add("core.quotepath=false")
                        .Add("-c").Add("core.safecrlf=false")
                        .Add("--literal-pathspecs")
                        .Add("diff").Add("--name-status").Add("--no-renames")
                        .Add(from.GitRef!);
                    if (!to.IsWorkingTree) args.Add(to.GitRef!);
                    args.Add("--").Add(srcRelPath);
                })
                .WithValidation(CommandResultValidation.None),
                capture, ct);

            AssertListed(nameStatusResult, "git diff --name-status");
            changedFiles = ParseNameStatus(nameStatusResult.StandardOutput);

            if (to.IsWorkingTree)
            {
                var statusResult = await Run(Porcelain(), capture, ct);
                AssertListed(statusResult, "git status --porcelain");
                var listed = changedFiles.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                changedFiles.AddRange(ParsePorcelain(statusResult.StandardOutput)
                    .Where(f => f.Untracked && !listed.Contains(f.Path)));
            }
        }

        changedFiles = changedFiles.Where(f => !IsExcluded(f.Path)).ToList();

        if (changedFiles.Count == 0)
            return new SolutionChangeSummary(0, 0, 0, []);

        var numstatResult = await Run(
            Cli.Wrap("git")
            .WithWorkingDirectory(repoRoot)
            .WithArguments(args =>
            {
                args.Add("-c").Add("core.quotepath=false")
                    .Add("-c").Add("core.safecrlf=false")
                    .Add("--literal-pathspecs")
                    .Add("diff").Add("--numstat").Add("--no-renames")
                    .Add(from.GitRef!);
                if (!to.IsWorkingTree) args.Add(to.GitRef!);
                args.Add("--").Add(srcRelPath);
            })
            .WithValidation(CommandResultValidation.None),
            capture, ct, suppressErrors: true);

        var linesByPath = numstatResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(p => p.Length >= 3 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .ToDictionary(p => p[2], p => (added: int.Parse(p[0]), removed: int.Parse(p[1])));

        var srcPrefix = srcRelPath.TrimEnd('/') + "/";
        var components = new Dictionary<string, (ParsedPath Parsed, List<string> Paths, List<ChangeStatus> FileStatuses)>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;
        int totalAdded = 0, totalRemoved = 0;
        var customizationsChanged = false;
        var solutionChanged = false;

        foreach (var (status, untracked, absPath) in changedFiles)
        {
            var relPath = absPath.StartsWith(srcPrefix) ? absPath[srcPrefix.Length..] : absPath;

            // Counted here, before the parseability gate below, so TotalFiles reflects every real change under
            // srcFolder — including paths ParseComponentPath doesn't know how to render as a ChangeItem (e.g.
            // Other/Solution.xml, or a non-Dataverse folder like GenerateCommand's Models/). Callers that gate
            // dirty-tree checks on TotalFiles must not silently pass just because a change can't be described.
            fileCount++;

            if (untracked)
            {
                var fullPath = Path.Combine(repoRoot, absPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                    totalAdded += CountLines(fullPath);
            }
            else if (linesByPath.TryGetValue(absPath, out var counts))
            {
                totalAdded += counts.added;
                totalRemoved += counts.removed;
            }

            if (relPath.Equals(CustomizationsRelPath, StringComparison.OrdinalIgnoreCase))
                customizationsChanged = true;

            if (relPath.Equals(SolutionRelPath, StringComparison.OrdinalIgnoreCase))
                solutionChanged = true;

            var parsed = ParseComponentPath(relPath);
            if (parsed == null) continue;

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
                var status = AggregateStatus(c.FileStatuses);
                var relPath = c.Paths[0];

                // Both the display name and the sub-change diff read the same component XML, so each side is
                // fetched at most once here and handed to both. A fetch is a `git show` per component, so
                // resolving them independently cost a Form or View up to three subprocesses for two answers.
                var needsName = c.Parsed.StaticName is null;
                var needsSubChanges = HasSubChanges(c.Parsed) && status != ChangeStatus.Added;

                string? newXml = null, oldXml = null;
                var readFailed = false;
                if ((needsName || needsSubChanges) && status != ChangeStatus.Deleted)
                    (newXml, readFailed) = await SideXml(to, relPath);
                if (needsSubChanges || (needsName && newXml is null))
                {
                    var old = await SideXml(from, relPath);
                    oldXml = old.Xml;
                    readFailed |= old.ReadFailed;
                }

                var name = c.Parsed.StaticName
                           ?? ResolveXmlName(newXml ?? oldXml, c.Parsed.XmlRead, c.Parsed.NameSuffix)
                           ?? c.Parsed.FallbackName ?? Path.GetFileNameWithoutExtension(relPath);
                // A side that couldn't be read is not an empty side: diffing against it would render every
                // attribute as Added. Report the component's own status and drop the sub-change block.
                var subChanges = needsSubChanges && !readFailed ? ResolveSubChanges(c.Parsed, oldXml, newXml) : null;
                items.Add(new ChangeItem(name, c.Paths, status, subChanges));
            }
            resolvedGroups.Add(new ChangeGroup(g.Key, items, g.First().Parsed.IsEntity));
        }

        if (customizationsChanged)
        {
            var oldCust = await SideXml(from, CustomizationsRelPath);
            var newCust = await SideXml(to, CustomizationsRelPath);
            // Same reason as the sub-changes above: an unreadable side would list every connection
            // reference as Added.
            if (!oldCust.ReadFailed && !newCust.ReadFailed)
            {
                var connRefItems = DiffConnectionReferences(oldCust.Xml, newCust.Xml);
                if (connRefItems is { Count: > 0 })
                    resolvedGroups.Add(new ChangeGroup("Connection References", connRefItems));
            }
        }

        VersionTransition? version = null;
        if (solutionChanged)
            version = DiffSolutionVersion(
                (await SideXml(from, SolutionRelPath)).Xml,
                (await SideXml(to, SolutionRelPath)).Xml);

        return new SolutionChangeSummary(fileCount, totalAdded, totalRemoved, resolvedGroups, version);
    }

    internal const string CustomizationsRelPath = "Other/Customizations.xml";
    internal const string SolutionRelPath = "Other/Solution.xml";

    /// <summary>The solution version sits in Other/Solution.xml, which has no component representation, so the
    /// transition is read element-wise from both sides. A version missing on either side means no transition.</summary>
    internal static VersionTransition? DiffSolutionVersion(string? oldXml, string? newXml)
    {
        static string? ReadVersion(string? xml)
        {
            if (xml == null) return null;
            try { return XmlHelpers.Parse(xml).Root?.Element("SolutionManifest")?.Element("Version")?.Value; }
            catch (XmlException) { return null; }
        }

        var oldVersion = ReadVersion(oldXml);
        var newVersion = ReadVersion(newXml);
        return string.IsNullOrEmpty(oldVersion) || string.IsNullOrEmpty(newVersion) || oldVersion == newVersion
            ? null
            : new VersionTransition(oldVersion, newVersion);
    }

    /// <summary>Connection references live inside Other/Customizations.xml, not their own files, so they need an
    /// element-level diff of that one file rather than the per-file component pipeline.</summary>
    internal static List<ChangeItem>? DiffConnectionReferences(string? oldXml, string? newXml)
    {
        var oldRefs = ParseXmlElements(oldXml, "connectionreference", e => (string?)e.Attribute("connectionreferencelogicalname"));
        var newRefs = ParseXmlElements(newXml, "connectionreference", e => (string?)e.Attribute("connectionreferencelogicalname"));
        if (oldRefs == null && newRefs == null) return null;

        static string Name(string logicalName, XElement el)
        {
            var display = (string?)el.Element("connectionreferencedisplayname");
            return string.IsNullOrWhiteSpace(display) || display == logicalName ? logicalName : $"{display} ({logicalName})";
        }

        var added = new List<ChangeItem>();
        var removed = new List<ChangeItem>();
        var modified = new List<ChangeItem>();

        foreach (var (logicalName, el) in newRefs ?? [])
        {
            if (!(oldRefs?.TryGetValue(logicalName, out var oldEl) ?? false))
                added.Add(new ChangeItem(Name(logicalName, el), [CustomizationsRelPath], ChangeStatus.Added));
            else if (oldEl.ToString() != el.ToString())
                modified.Add(new ChangeItem(Name(logicalName, el), [CustomizationsRelPath], ChangeStatus.Modified));
        }
        foreach (var (logicalName, el) in oldRefs ?? [])
            if (!(newRefs?.ContainsKey(logicalName) ?? false))
                removed.Add(new ChangeItem(Name(logicalName, el), [CustomizationsRelPath], ChangeStatus.Deleted));

        return [..added, ..removed, ..modified];
    }

    /// <summary>Writes the report to <paramref name="outputPath"/>.</summary>
    /// <remarks>
    /// The caller supplies the target and the provenance line: what the two compared points were is
    /// command-specific — an environment for <c>sync</c>, two git points for <c>diff</c> — and so is whether
    /// an empty report is worth a file at all.
    /// </remarks>
    public async Task WriteChangesFileAsync(string outputPath, string solutionName, string? provenanceLine,
        bool writeWhenEmpty = false, CancellationToken ct = default)
    {
        if (TotalFiles == 0 && !writeWhenEmpty) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Changes — {solutionName} ({DateTime.Now:yyyy-MM-dd})");
        sb.AppendLine();
        if (provenanceLine != null)
        {
            sb.AppendLine(provenanceLine);
            sb.AppendLine();
        }
        if (TotalFiles == 0)
        {
            sb.AppendLine("No changes.");
            await WriteFileAsync(outputPath, sb.ToString(), ct);
            return;
        }
        if (Version is { } version)
        {
            sb.AppendLine($"Version {version.From} -> {version.To}");
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

        await WriteFileAsync(outputPath, sb.ToString(), ct);
    }

    static Task WriteFileAsync(string outputPath, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return File.WriteAllTextAsync(outputPath, content, ct);
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

    /// <summary>Renders the report. <paramref name="noChangesMessage"/> is the complete, already-escaped line
    /// shown when nothing changed — the caller owns it, because only the caller knows what was compared.
    /// <paramref name="overflowHint"/> is the already-escaped parenthetical appended to a truncated sub-change
    /// list, naming where the rest can be read. Only the caller knows whether such a place exists — the report
    /// file is written on request, not on every run — so a null hint truncates without offering a route.
    /// <c>--verbose</c> does not lift the cap, so the hint is the only route the user is offered.</summary>
    public void WriteTree(IAnsiConsole console, string noChangesMessage, bool verbose, string? overflowHint = null)
    {
        if (TotalFiles == 0)
        {
            console.Info(noChangesMessage);
            return;
        }

        var headline = $"\nChanges ({TotalFiles} {(TotalFiles == 1 ? "file" : "files")}, +{LinesAdded} -{LinesRemoved})";
        if (Version is { } version)
            headline += $"\nVersion {version.From} -> {version.To}";
        var tree = new Tree(headline);

        var entityGroups = Groups.Where(g => g.IsEntity).ToList();
        var otherGroups = Groups.Where(g => !g.IsEntity).ToList();

        if (entityGroups.Count > 0)
        {
            var entitiesNode = tree.AddNode("Entities");
            foreach (var group in entityGroups)
                AddGroupItems(entitiesNode.AddNode(Markup.Escape(group.Label)), group, verbose, overflowHint);
        }

        foreach (var group in otherGroups)
            AddGroupItems(tree.AddNode(Markup.Escape(group.Label)), group, verbose, overflowHint);

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

    static void AddGroupItems(TreeNode groupNode, ChangeGroup group, bool verbose, string? overflowHint)
    {
        if (group.Label == "Web Resources")
        {
            AddFileTreeNodes(groupNode, group.Items, verbose);
            return;
        }
        foreach (var item in group.Items)
        {
            var label = $"{StatusIcon(item.Status)} {Markup.Escape(item.ComponentName)}";

            var itemNode = groupNode.AddNode(label);

            if (item.SubChanges is { Count: > 0 })
            {
                var shown = item.SubChanges.Take(SubChangeDisplayThreshold).ToList();
                foreach (var sub in shown)
                    itemNode.AddNode($"{StatusIcon(sub.Status)} {Markup.Escape(sub.Description)}");
                var overflow = item.SubChanges.Count - shown.Count;
                if (overflow > 0)
                    itemNode.AddNode($"[dim]...and {overflow} more{(overflowHint is null ? "" : $" ({overflowHint})")}[/]");
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

    static Task<CliWrap.Buffered.BufferedCommandResult> Run(Command cmd, SubprocessCapture? cap, CancellationToken ct, bool suppressErrors = false) =>
        (cap?.Apply(cmd, suppressErrors: suppressErrors) ?? cmd).ExecuteBufferedAsync(ct);

    /// <summary>A file listed by either vocabulary, already normalized: git status porcelain (two-character
    /// codes, "??" for untracked) and git diff --name-status (single letters, renames disabled).</summary>
    record struct ChangedFile(ChangeStatus Status, bool Untracked, string Path);

    static List<ChangedFile> ParsePorcelain(string output) =>
        output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 3)
            .Select(line => new ChangedFile(
                ClassifyFileStatus(line[..2]), line[..2] == "??", line[3..].Replace('\\', '/')))
            .ToList();

    static List<ChangedFile> ParseNameStatus(string output) =>
        output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(p => p.Length >= 2 && p[0].Length > 0)
            .Select(p => new ChangedFile(
                p[0][0] switch { 'A' => ChangeStatus.Added, 'D' => ChangeStatus.Deleted, _ => ChangeStatus.Modified },
                false,
                p[1].Replace('\\', '/')))
            .ToList();

    static async Task EnsureRefExistsAsync(string gitRef, string workingDirectory, SubprocessCapture? capture, CancellationToken ct)
    {
        var cmd = Cli.Wrap("git")
            .WithWorkingDirectory(workingDirectory)
            .WithArguments(args => args.Add("rev-parse").Add("--verify").Add("--quiet").Add(gitRef + "^{commit}"))
            .WithValidation(CommandResultValidation.None);
        var result = await Run(cmd, capture, ct, suppressErrors: true);
        if (result.ExitCode != 0)
            throw new FlowlineException(ExitCode.NotFound,
                $"No git ref named '{gitRef}'. List the refs you can compare with: git log --oneline");
    }

    static ChangeStatus ClassifyFileStatus(string gitStatus) =>
        gitStatus == "??" || gitStatus[0] == 'A' || gitStatus[1] == 'A' ? ChangeStatus.Added :
        gitStatus[0] == 'D' || gitStatus[1] == 'D' ? ChangeStatus.Deleted :
        ChangeStatus.Modified;

    static ChangeStatus AggregateStatus(IEnumerable<ChangeStatus> fileStatuses)
    {
        var classified = fileStatuses.Distinct().ToList();
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

    /// <summary>Whether this component type has a sub-change diff at all. Everything else reports only its own status.</summary>
    static bool HasSubChanges(ParsedPath parsed) =>
        parsed.ComponentKey.EndsWith("/entity", StringComparison.OrdinalIgnoreCase)
        || parsed.XmlRead is XmlRead.ViewTitle or XmlRead.FormTitle
        || string.Equals(parsed.Group, "OptionSets", StringComparison.OrdinalIgnoreCase);

    static IReadOnlyList<SubChange>? ResolveSubChanges(ParsedPath parsed, string? oldXml, string? newXml)
    {
        bool isEntityMeta = parsed.ComponentKey.EndsWith("/entity", StringComparison.OrdinalIgnoreCase);
        bool isView       = parsed.XmlRead == XmlRead.ViewTitle;
        bool isForm       = parsed.XmlRead == XmlRead.FormTitle;

        if (oldXml == null && newXml == null) return null;

        var result = isEntityMeta ? DiffEntityAttributes(oldXml, newXml)
                   : isView       ? DiffSavedQuery(oldXml, newXml)
                   : isForm       ? DiffFormXml(oldXml, newXml)
                   :                DiffOptionSet(oldXml, newXml);

        return result is { Count: > 0 } ? result : null;
    }

    /// <summary>One side's file content. <see cref="ReadFailed"/> separates "the read broke" from
    /// "the file genuinely isn't on that side" — both arrive as a null <see cref="Xml"/>, but only the
    /// second one may be diffed against.</summary>
    readonly record struct SideXmlResult(string? Xml, bool ReadFailed);

    static async Task<SideXmlResult> GetSideXmlAsync(
        ComparisonSide side, string relPath, string srcFolder, string srcRelPath,
        string repoRoot, SubprocessCapture? capture, CancellationToken ct)
    {
        if (side.IsWorkingTree)
        {
            var fullPath = Path.Combine(srcFolder, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) return new SideXmlResult(null, false);
            try { return new SideXmlResult(await File.ReadAllTextAsync(fullPath, ct), false); }
            catch (OperationCanceledException) { throw; }
            catch { return new SideXmlResult(null, true); }
        }

        // Repo-root-relative: `git show <ref>:<path>` resolves from the top of the tree, not from the
        // process working directory.
        var gitPath = srcRelPath.TrimEnd('/') + "/" + relPath;

        // Deliberately NOT routed through Run/SubprocessCapture, unlike every other git call here.
        // Capture pipes each stdout line through the console renderer and the log sink, which is right
        // for a listing of a few lines and wrong for this: stdout is the whole component file. One
        // solution manifest is tens of thousands of lines, fetched once per side, so routing it through
        // capture put minutes of rendering on the main path. This is file content, not tool output.
        var cmd = Cli.Wrap("git")
            .WithWorkingDirectory(repoRoot)
            .WithArguments(args => args
                .Add("-c").Add("core.quotepath=false")
                .Add("show").Add($"{side.GitRef}:{gitPath}"))
            // git localizes its fatal messages, and the absent-vs-failed split below reads them.
            .WithEnvironmentVariables(e => e.Set("LC_ALL", "C"))
            .WithValidation(CommandResultValidation.None);
        var result = await cmd.ExecuteBufferedAsync(ct);

        if (result.ExitCode == 0) return new SideXmlResult(XmlHelpers.StripBom(result.StandardOutput), false);

        // git says "does not exist in" / "exists on disk, but not in" for a path absent from the ref;
        // any other fatal (broken object, unreadable repo) is a read failure, not an absence.
        var absent = result.StandardError.Contains("does not exist in", StringComparison.Ordinal)
                     || result.StandardError.Contains("exists on disk, but not in", StringComparison.Ordinal);
        return new SideXmlResult(null, !absent);
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
