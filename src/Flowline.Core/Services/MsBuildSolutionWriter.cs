using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Flowline.Core.Services;

/// <summary>What a call to <see cref="MsBuildSolutionWriter.AddProjectAsync"/> changed on disk.</summary>
/// <param name="Created"><c>true</c> when there was no solution file and the writer made one.</param>
/// <param name="Added"><c>true</c> when an entry was written; <c>false</c> when the solution already referenced the project.</param>
/// <remarks>
/// Both flags are decided inside the writer because it is the only place that can decide them without a
/// race: it already stats the file to pick its write path, so a caller doing its own <c>File.Exists</c>
/// first is both a duplicate and a TOCTOU window — a concurrent create between the two calls would have
/// the caller report a creation that never happened.
/// </remarks>
public readonly record struct SolutionWriteResult(bool Created, bool Added);

/// <summary>
/// Writes project entries into MSBuild solution files (<c>.sln</c> and <c>.slnx</c>).
/// </summary>
/// <remarks>
/// Exists because <c>dotnet sln add</c> refuses a <c>.cdsproj</c> — it cannot resolve a project type
/// GUID for extensions it does not recognize, and it exits 0 while doing so, so a caller cannot even
/// detect the failure (https://github.com/dotnet/sdk/issues/47638). Writing the entry ourselves is
/// what makes the Dataverse package project reachable from a normal <c>dotnet build</c>.
///
/// "Solution" here means the MSBuild solution file, not a Dataverse solution — see
/// <see cref="MsBuildSolutionReader"/> for the same naming rule.
/// </remarks>
public class MsBuildSolutionWriter
{
    /// <summary>The project type passed to the library for every entry Flowline writes.</summary>
    /// <remarks>
    /// Mandatory, not decorative. <c>AddProject(path)</c> without a type throws
    /// <c>SolutionArgumentException: ProjectType '' not found</c> for a <c>.cdsproj</c>, because the
    /// library resolves the type from the extension and knows nothing about that one. "C#" maps to the
    /// C# project type GUID, which is what makes MSBuild load a <c>.cdsproj</c> like any other project.
    /// </remarks>
    const string CSharpProjectType = "C#";

    /// <summary>The legacy C# project type GUID, written verbatim by the surgical <c>.sln</c> path.</summary>
    /// <remarks>
    /// This is the GUID the library itself emits for a <see cref="CSharpProjectType"/> entry in a
    /// <c>.sln</c>, so the two paths stay indistinguishable in their output. MSBuild loads a
    /// <c>.cdsproj</c> like any other project once it carries this.
    /// </remarks>
    const string LegacyCSharpProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    /// <summary>Matches the bare <c>Global</c> line — the anchor the project block is inserted before.</summary>
    /// <remarks>
    /// Anchored on <c>Global</c> rather than the last <c>EndProject</c> because a solution with zero
    /// projects has no <c>EndProject</c> to anchor against. The trailing <c>[ \t]*</c> keeps
    /// <c>GlobalSection</c> and <c>EndGlobal</c> from matching.
    /// </remarks>
    static readonly Regex s_globalLine = new(@"^Global[ \t]*\r?$", RegexOptions.Multiline);

    /// <summary>Captures a <c>GlobalSection</c> by name, including its <c>EndGlobalSection</c> line.</summary>
    static Regex SectionRegex(string name) => new(
        $@"^(?<indent>[ \t]*)GlobalSection\({name}\)[^\r\n]*\r?\n(?<body>.*?)^[ \t]*EndGlobalSection[ \t]*\r?\n?",
        RegexOptions.Multiline | RegexOptions.Singleline);

    /// <summary>
    /// Adds <paramref name="projectPath"/> to the solution file at <paramref name="solutionFilePath"/>,
    /// creating that file when it does not exist yet.
    /// </summary>
    /// <param name="solutionFilePath">Full path to the solution file. Its extension decides the format written.</param>
    /// <param name="projectPath">Path to the project, relative to the folder holding the solution file.</param>
    /// <returns>Whether the solution file had to be created, and whether an entry was written.</returns>
    /// <remarks>
    /// The single entry point, and the decider between two very different write strategies, so callers
    /// need no knowledge of the split:
    /// <list type="bullet">
    /// <item>any <c>.slnx</c>, and a <c>.sln</c> that does not exist yet — full round-trip through the
    /// library, which is safe because there is either nothing to preserve or a format the library was
    /// designed to preserve;</item>
    /// <item>a <c>.sln</c> that <b>does</b> exist — surgical text insert, never the library. Its
    /// <c>.sln</c> writer discards each project's parsed display name and re-derives it from the
    /// filename (https://github.com/microsoft/vs-solutionpersistence/issues/122), which silently breaks
    /// <c>msbuild -t:&lt;Target&gt;</c> for anyone whose solution named a project differently from its
    /// file. Re-assigning the display name before save does not prevent it — that was tested.</item>
    /// </list>
    ///
    /// Accepts any project extension. Refusing a <c>.csproj</c> (which <c>dotnet sln add</c> handles fine)
    /// is a command-level concern, not the writer's.
    /// </remarks>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ConfigInvalid"/> when the path is not a solution file, when an existing file is
    /// malformed, or when the library rejects the entry.
    /// </exception>
    public async Task<SolutionWriteResult> AddProjectAsync(
        string solutionFilePath,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath)
            ?? throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{Path.GetFileName(solutionFilePath)}' is not a solution file — expected .sln or .slnx.");

        // The library's duplicate check compares raw strings, so a path that differs only by separator
        // reads as a second project to it. Normalizing here is what makes re-adding a no-op.
        var normalizedPath = MsBuildSolutionReader.NormalizePath(projectPath);

        // Stat once and reuse. Both the write-path choice and the caller's "did I create it" answer come
        // off this one observation, so the two can never disagree about the file they saw.
        var exists = File.Exists(solutionFilePath);

        var isPreExistingSln = exists
            && string.Equals(Path.GetExtension(solutionFilePath), ".sln", StringComparison.OrdinalIgnoreCase);

        var added = isPreExistingSln
            ? await InsertIntoExistingSlnAsync(solutionFilePath, normalizedPath, cancellationToken).ConfigureAwait(false)
            : await AddViaSolutionModelAsync(serializer, solutionFilePath, exists, normalizedPath, projectPath, cancellationToken)
                .ConfigureAwait(false);

        return new SolutionWriteResult(Created: !exists, Added: added);
    }

    /// <summary>
    /// Adds the entry by loading (or creating) the library's model and saving it back — a full rewrite of
    /// the file.
    /// </summary>
    /// <remarks>
    /// Only safe where nothing hand-written is at stake. See <see cref="AddProjectAsync"/> for why a
    /// pre-existing <c>.sln</c> is routed elsewhere.
    /// </remarks>
    static async Task<bool> AddViaSolutionModelAsync(
        ISolutionSerializer serializer,
        string solutionFilePath,
        bool exists,
        string normalizedPath,
        string projectPath,
        CancellationToken cancellationToken)
    {
        SolutionModel model;

        if (exists)
        {
            model = await MsBuildSolutionReader.OpenAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);
            if (ContainsProject(model, normalizedPath)) return false;
        }
        else
        {
            model = CreateModel();
        }

        try
        {
            model.AddProject(normalizedPath, CSharpProjectType);
        }
        catch (SolutionArgumentException ex)
        {
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"Couldn't add '{projectPath}' to '{Path.GetFileName(solutionFilePath)}' — {ex.Message}", ex);
        }

        try
        {
            await serializer.SaveAsync(solutionFilePath, model, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The usual cause is the solution file being open in an IDE, which holds a lock. Without
            // this the user gets a stack trace for something they can fix by closing a window.
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"Couldn't write '{Path.GetFileName(solutionFilePath)}' — {ex.Message}", ex);
        }

        return true;
    }

    /// <summary>
    /// Adds the entry to an existing <c>.sln</c> by splicing text, leaving every other byte untouched.
    /// </summary>
    /// <remarks>
    /// The membership check still goes through the parser rather than a substring search: a raw string
    /// compare misses the same file spelled with the other separator or different casing, and would then
    /// write a duplicate entry.
    /// </remarks>
    static async Task<bool> InsertIntoExistingSlnAsync(
        string solutionFilePath,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        var model = await MsBuildSolutionReader.OpenAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);
        if (ContainsProject(model, normalizedPath)) return false;

        // Read as bytes so the file's BOM (or absence of one) survives. Round-tripping through
        // File.ReadAllText/WriteAllText would strip an existing UTF-8 BOM and show up as a whole-file
        // diff in git — precisely what this path exists to avoid.
        var bytes = await File.ReadAllBytesAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        string text;
        try
        {
            // Throw rather than substitute. The permissive decoder turns any non-UTF8 byte into U+FFFD,
            // and this path writes the decoded text straight back — so a legacy ANSI-encoded solution
            // with a non-ASCII project name would come back with its characters silently destroyed.
            // Refusing the file leaves it intact and tells the user what is wrong with it.
            text = new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException ex)
        {
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{Path.GetFileName(solutionFilePath)}' isn't valid UTF-8 — re-save it as UTF-8 and try again.", ex);
        }

        var updated = InsertProjectEntry(text, normalizedPath, Guid.NewGuid());

        await ReplaceFileAsync(solutionFilePath, updated, hasBom, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Returns <paramref name="text"/> with a project entry and its configuration rows spliced in.</summary>
    /// <remarks>
    /// Pure string work, no IO, so the whole insert is testable and so a failure cannot leave a partial
    /// file behind. The configuration rows are inserted before the project block even though they sit
    /// later in the file: both offsets are computed against the original text, and splicing the later one
    /// first keeps the earlier offset valid.
    /// </remarks>
    internal static string InsertProjectEntry(string text, string normalizedPath, Guid projectGuid)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var guid = projectGuid.ToString("B").ToUpperInvariant();

        var withRows = InsertConfigurationRows(text, guid, newline);
        return InsertProjectBlock(withRows, guid, normalizedPath, newline);
    }

    /// <summary>Splices the <c>Project(...)</c>/<c>EndProject</c> pair in immediately before <c>Global</c>.</summary>
    /// <remarks>
    /// A file with no <c>Global</c> line at all is degraded but valid — it simply declares no solution
    /// configurations. Rather than fabricating a <c>Global</c> section the user never had, the entry is
    /// appended at the end: the file stays parseable and gains the project, which is the caller's actual
    /// goal.
    /// </remarks>
    static string InsertProjectBlock(string text, string guid, string normalizedPath, string newline)
    {
        // The .sln format stores backslash-separated paths on every host OS.
        var slnPath = normalizedPath.Replace(Path.DirectorySeparatorChar, '\\').Replace('/', '\\');
        var displayName = Path.GetFileNameWithoutExtension(normalizedPath);
        var block =
            $"Project(\"{LegacyCSharpProjectTypeGuid}\") = \"{displayName}\", \"{slnPath}\", \"{guid}\"{newline}" +
            $"EndProject{newline}";

        var global = s_globalLine.Match(text);
        if (global.Success) return text.Insert(global.Index, block);

        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : newline;
        return text + separator + block;
    }

    /// <summary>
    /// Splices <c>ProjectConfigurationPlatforms</c> rows for the new project, one <c>ActiveCfg</c> and one
    /// <c>Build.0</c> per configuration/platform pair the file already declares.
    /// </summary>
    /// <remarks>
    /// The pairs come from the file's own <c>SolutionConfigurationPlatforms</c> rather than an assumed
    /// Debug/Release × Any CPU: a solution that only declares <c>Debug|x64</c> must not gain rows for
    /// configurations it does not have. Without matching rows the project shows in the solution but never
    /// builds, which is the whole reason for writing the entry.
    ///
    /// No declared pairs (no <c>Global</c>, or no <c>SolutionConfigurationPlatforms</c>) means there is
    /// nothing to map to, so no rows are written and no section is invented.
    ///
    /// The two sides of each row mean different things and must not be the same string. The left is the
    /// <b>solution</b> configuration, copied verbatim from what the file declares. The right is the
    /// <b>project</b> configuration to build for it, and a <c>.cdsproj</c> builds Any CPU. Verified against
    /// <c>dotnet sln add</c> on SDK 10: for a solution declaring <c>Debug|x64</c> it writes
    /// <c>{guid}.Debug|x64.ActiveCfg = Debug|Any CPU</c>.
    ///
    /// What repeating the solution platform on the right actually costs, measured against a real
    /// <c>pac solution init</c> project: the build still <i>succeeds</i> — MSBuild honours
    /// <c>Platform=x64</c> and a <c>.cdsproj</c> compiles nothing that could reject it — but the packed
    /// zip lands in <c>bin\x64\Debug\</c> instead of <c>bin\Debug\</c>. So this is about matching the
    /// first-party layout every doc and script expects, not about preventing a build failure. Recorded
    /// because "the build breaks" is the intuitive guess and it is wrong.
    ///
    /// Two deliberate divergences from <c>dotnet sln add</c>:
    /// <list type="bullet">
    /// <item>It falls back to <c>Debug|Any CPU</c> for a build type the project lacks (so a declared
    /// <c>CustomCfg|ARM64</c> gets <c>Debug|Any CPU</c>). We keep the declared name and write
    /// <c>CustomCfg|Any CPU</c>. The two differ only for names outside Debug/Release, where the project
    /// has no matching configuration either way — and preserving what the user named is less surprising
    /// than silently substituting Debug.</item>
    /// <item>It also cross-produces every build type against every platform, turning 3 declared pairs into
    /// 12 rows and inventing <c>Any CPU</c> and <c>x86</c> entries the solution never had. Rewriting the
    /// user's declared configuration set is exactly what this surgical path exists to avoid, so one row
    /// pair per declared pair is all that is written.</item>
    /// </list>
    /// </remarks>
    static string InsertConfigurationRows(string text, string guid, string newline)
    {
        var solutionSection = SectionRegex("SolutionConfigurationPlatforms").Match(text);
        if (!solutionSection.Success) return text;

        var pairs = ReadConfigurationPairs(solutionSection.Groups["body"].Value);
        if (pairs.Count == 0) return text;

        var sectionIndent = solutionSection.Groups["indent"].Value;
        // Row indentation is taken from the sibling section's rows so tabs-vs-spaces follows the file.
        var rowIndent = LeadingWhitespace(solutionSection.Groups["body"].Value) ?? sectionIndent + "\t";

        var rows = new StringBuilder();
        foreach (var pair in pairs)
        {
            var projectConfig = ToProjectConfiguration(pair);
            rows.Append(rowIndent).Append(guid).Append('.').Append(pair).Append(".ActiveCfg = ").Append(projectConfig).Append(newline);
            rows.Append(rowIndent).Append(guid).Append('.').Append(pair).Append(".Build.0 = ").Append(projectConfig).Append(newline);
        }

        var projectSection = SectionRegex("ProjectConfigurationPlatforms").Match(text);
        if (projectSection.Success)
        {
            // Append to the existing section, just past its last row and before EndGlobalSection.
            var insertAt = projectSection.Groups["body"].Index + projectSection.Groups["body"].Length;
            return text.Insert(insertAt, rows.ToString());
        }

        // No section yet: create one directly after SolutionConfigurationPlatforms, which is where both
        // Visual Studio and the library put it.
        var newSection =
            $"{sectionIndent}GlobalSection(ProjectConfigurationPlatforms) = postSolution{newline}" +
            rows +
            $"{sectionIndent}EndGlobalSection{newline}";

        return text.Insert(solutionSection.Index + solutionSection.Length, newSection);
    }

    /// <summary>Maps a declared solution pair to the project configuration it should build.</summary>
    /// <remarks>
    /// Keeps the build-type half and replaces the platform with <c>Any CPU</c>, which is what the projects
    /// Flowline writes actually build. A pair with no <c>|</c> is malformed rather than meaningful, so the
    /// whole string is treated as the build type instead of guessing at where it was meant to split.
    /// </remarks>
    static string ToProjectConfiguration(string declaredPair)
    {
        var separator = declaredPair.IndexOf('|');
        var buildType = separator < 0 ? declaredPair : declaredPair[..separator];
        return $"{buildType}|Any CPU";
    }

    /// <summary>Reads the left-hand side of each <c>Debug|Any CPU = Debug|Any CPU</c> row in a section body.</summary>
    static List<string> ReadConfigurationPairs(string body) =>
        [.. body.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && line.Contains('='))
                .Select(line => line[..line.IndexOf('=')].Trim())
                .Where(pair => pair.Length > 0)];

    /// <summary>The indentation of the first non-blank line, or <c>null</c> when the body holds none.</summary>
    static string? LeadingWhitespace(string body) =>
        body.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Trim().Length > 0)
            .Select(line => line[..(line.Length - line.TrimStart(' ', '\t').Length)])
            .FirstOrDefault();

    /// <summary>Writes <paramref name="content"/> to a sibling temp file, then swaps it into place.</summary>
    /// <remarks>
    /// A direct write leaves a window where a crash or Ctrl+C truncates the user's solution file to
    /// nothing. Writing beside it and moving keeps the replacement close to atomic, and the temp file
    /// shares the directory so the move never crosses a volume.
    /// </remarks>
    static async Task ReplaceFileAsync(string solutionFilePath, string content, bool hasBom, CancellationToken cancellationToken)
    {
        // Unique per write: a fixed name collides when two writes race, and the loser would move the
        // winner's content over the target.
        var tempPath = $"{solutionFilePath}.{Guid.NewGuid():N}.flowline-tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(hasBom), cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, solutionFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"Couldn't write '{Path.GetFileName(solutionFilePath)}' — {ex.Message}", ex);
        }
        finally
        {
            // Covers the failed write, the failed move, and cancellation — none of which should leave
            // a stray file sitting next to the user's solution.
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// True when <paramref name="model"/> already references <paramref name="normalizedPath"/>,
    /// comparing separator- and case-insensitively.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>SolutionModel.FindProject</c>: that one is an exact string match, so it misses
    /// <c>Solution\X.cdsproj</c> when the file stores <c>Solution/X.cdsproj</c>
    /// (https://github.com/microsoft/vs-solutionpersistence/issues/134). Checking first also keeps the
    /// duplicate case out of exception-driven control flow.
    /// </remarks>
    static bool ContainsProject(SolutionModel model, string normalizedPath) =>
        model.SolutionProjects.Any(p => MsBuildSolutionReader.PathEquals(p.FilePath, normalizedPath));

    /// <summary>Builds an empty model ready to take projects.</summary>
    /// <remarks>
    /// The platform and build types must be registered before any project is added, or the model ends up
    /// in a state the serializers cannot write correctly
    /// (https://github.com/microsoft/vs-solutionpersistence/issues/132). Debug/Release × Any CPU matches
    /// what the SDK templates produce.
    /// </remarks>
    static SolutionModel CreateModel()
    {
        var model = new SolutionModel();
        model.AddPlatform("Any CPU");
        model.AddBuildType("Debug");
        model.AddBuildType("Release");
        return model;
    }
}
