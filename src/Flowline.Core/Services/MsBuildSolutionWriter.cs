using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Flowline.Core.Services;

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

    /// <summary>
    /// Adds <paramref name="projectPath"/> to the solution file at <paramref name="solutionFilePath"/>,
    /// creating that file when it does not exist yet.
    /// </summary>
    /// <param name="solutionFilePath">Full path to the solution file. Its extension decides the format written.</param>
    /// <param name="projectPath">Path to the project, relative to the folder holding the solution file.</param>
    /// <returns><c>true</c> when an entry was written; <c>false</c> when the solution already referenced the project.</returns>
    /// <remarks>
    /// Covers the two cases where a full round-trip through the library is safe: any <c>.slnx</c>, and a
    /// <c>.sln</c> Flowline is creating from nothing. Adding to a <b>pre-existing</b> <c>.sln</c> is
    /// deliberately not routed here — the library's <c>.sln</c> writer re-derives each project's display
    /// name from its filename, which silently breaks <c>msbuild -t:&lt;Target&gt;</c> for anyone whose
    /// solution named a project differently. That case gets a surgical text insert instead, and the
    /// decision between the two paths lands with it.
    ///
    /// Accepts any project extension. Refusing a <c>.csproj</c> (which <c>dotnet sln add</c> handles fine)
    /// is a command-level concern, not the writer's.
    /// </remarks>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ConfigInvalid"/> when the path is not a solution file, when an existing file is
    /// malformed, or when the library rejects the entry.
    /// </exception>
    public async Task<bool> AddProjectAsync(
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

        SolutionModel model;

        if (File.Exists(solutionFilePath))
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

        await serializer.SaveAsync(solutionFilePath, model, cancellationToken).ConfigureAwait(false);
        return true;
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
        model.SolutionProjects.Any(p => string.Equals(
            MsBuildSolutionReader.NormalizePath(p.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));

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
