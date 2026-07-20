using System.Xml;
using Flowline.Core.Models;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Flowline.Core.Services;

/// <summary>
/// Locates and reads MSBuild solution files (<c>.sln</c> and <c>.slnx</c>).
/// </summary>
/// <remarks>
/// The solution file is Flowline's authoritative record of which projects belong to a project —
/// the package project, the plugin projects, and the web resources project are all discovered
/// through it rather than through fixed folder names.
///
/// Not to be confused with <see cref="SolutionReader"/>, which reads *Dataverse* solution records.
/// Every type here carries the <c>MsBuild</c> prefix for exactly that reason.
/// </remarks>
public class MsBuildSolutionReader
{
    /// <summary>Solution file extensions, in preference order when a folder holds more than one.</summary>
    /// <remarks>
    /// <c>.slnx</c> wins: <c>dotnet sln migrate</c> writes the <c>.slnx</c> but leaves the original
    /// <c>.sln</c> behind, and the migrated file is the one the user meant to keep.
    /// </remarks>
    static readonly string[] s_extensionsByPreference = [".slnx", ".sln"];

    /// <summary>
    /// Finds the solution file directly inside <paramref name="folder"/>.
    /// </summary>
    /// <returns>The full path, or <c>null</c> when the folder holds no solution file.</returns>
    /// <remarks>
    /// Returning <c>null</c> rather than throwing is deliberate: callers such as
    /// <c>flowline sln add</c> treat "no solution file yet" as a state to create, not an error.
    /// </remarks>
    public string? FindSolutionFile(string folder) => FindAllSolutionFiles(folder).FirstOrDefault();

    /// <summary>Every solution file directly inside <paramref name="folder"/>, most-preferred first.</summary>
    /// <remarks>
    /// Filtering on the real extension matters: <c>EnumerateFiles("*.sln")</c> can also match a
    /// <c>.slnx</c> through 8.3 short-name expansion on some Windows volumes, which would make a folder
    /// holding only a <c>.slnx</c> look like it holds both.
    /// </remarks>
    public IReadOnlyList<string> FindAllSolutionFiles(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        return s_extensionsByPreference
               .SelectMany(ext => Directory.EnumerateFiles(folder, $"*{ext}", SearchOption.TopDirectoryOnly)
                                           .Where(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
                                           .Order(StringComparer.OrdinalIgnoreCase))
               .ToList();
    }

    /// <summary>True when <paramref name="folder"/> holds more than one solution file.</summary>
    /// <remarks>
    /// Not an error: <see cref="FindSolutionFile"/> picks one deterministically. Commands surface it so the
    /// user knows to remove the extra, because a bare <c>dotnet build</c> in such a folder fails with MSB1011.
    ///
    /// Deliberately not limited to a same-base-name pair. <c>dotnet sln migrate</c> produces that pair, but
    /// <c>App.slnx</c> beside <c>Zeta.sln</c> is just as ambiguous to the build — and writing to whichever
    /// sorts first while staying silent is the worse outcome.
    /// </remarks>
    public bool HasCoexistingSolutionFiles(string folder) => FindAllSolutionFiles(folder).Count > 1;

    /// <summary>
    /// Reads every project entry from the solution file at <paramref name="solutionFilePath"/>.
    /// </summary>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when the file does not exist, or
    /// <see cref="ExitCode.ConfigInvalid"/> when it is malformed or not a supported solution format.
    /// </exception>
    public async Task<IReadOnlyList<MsBuildSolutionProject>> ReadProjectsAsync(
        string solutionFilePath,
        CancellationToken cancellationToken = default)
    {
        var model = await OpenAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);

        return model.SolutionProjects
                    .Select(p => new MsBuildSolutionProject(
                        NormalizePath(p.FilePath),
                        // DisplayName is what the file declares; ActualDisplayName is re-derived from the
                        // filename and so disagrees for any project the solution named differently. The
                        // declared name is the one `msbuild -t:<Target>` resolves, so it is the truthful one.
                        p.DisplayName ?? p.ActualDisplayName,
                        Path.GetExtension(p.FilePath).ToLowerInvariant()))
                    .ToList();
    }

    /// <summary>
    /// Finds a project in the solution file by path, comparing separator- and case-insensitively.
    /// </summary>
    /// <returns>The matching entry, or <c>null</c> when the solution file does not reference it.</returns>
    /// <remarks>
    /// The underlying library compares raw strings, so <c>Solution\X.cdsproj</c> and
    /// <c>Solution/X.cdsproj</c> read as different projects to it
    /// (https://github.com/microsoft/vs-solutionpersistence/issues/134). Normalizing both sides here is
    /// what makes "already present" checks reliable.
    /// </remarks>
    public async Task<MsBuildSolutionProject?> FindProjectAsync(
        string solutionFilePath,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var projects = await ReadProjectsAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);

        return projects.FirstOrDefault(p => PathEquals(p.Path, projectPath));
    }

    /// <summary>
    /// Opens a solution file into the library's model, mapping its failures to <see cref="FlowlineException"/>.
    /// </summary>
    internal static async Task<SolutionModel> OpenAsync(string solutionFilePath, CancellationToken cancellationToken)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath)
            ?? throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{Path.GetFileName(solutionFilePath)}' is not a solution file — expected .sln or .slnx.");

        if (!File.Exists(solutionFilePath))
            throw new FlowlineException(ExitCode.NotFound, $"Solution file not found: {solutionFilePath}");

        try
        {
            return await serializer.OpenAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SolutionException or SolutionArgumentException or XmlException or IOException or UnauthorizedAccessException)
        {
            // SolutionException derives from FormatException and carries Line/Column; the argument
            // variant surfaces when a parse error stems from model validation (duplicate GUIDs, etc.).
            // XmlException is the one the library does NOT wrap — malformed .slnx propagates straight
            // out of XmlDocument.Load, so without this it would reach the user as an unhandled crash.
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"Couldn't read '{Path.GetFileName(solutionFilePath)}' — {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Rewrites a project path to one canonical separator so the two formats compare equal.
    /// </summary>
    /// <remarks>
    /// <c>.slnx</c> yields backslashes and <c>.sln</c> yields forward slashes on Windows. Callers compare
    /// these against on-disk locations, so the difference must not escape this class.
    /// </remarks>
    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar)
                             .Replace('/', Path.DirectorySeparatorChar);

        // Only a relative path may be stripped. Trimming unconditionally silently re-roots an absolute
        // POSIX path ("/opt/x.csproj" becomes "opt/x.csproj") and mangles a UNC share
        // (@"\\server\share\P.cdsproj" becomes @"server\share\P.cdsproj").
        //
        // IsPathFullyQualified, not IsPathRooted: on Windows a bare leading separator is "rooted"
        // (drive-relative), but @"\Plugins\X.csproj" still names the same project as "Plugins\X.csproj"
        // and must normalize to it, or the already-present check misses.
        return Path.IsPathFullyQualified(normalized)
            ? normalized
            : normalized.TrimStart(Path.DirectorySeparatorChar);
    }

    /// <summary>Compares two project paths the way a solution file means them: separator- and case-insensitively.</summary>
    internal static bool PathEquals(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
}
