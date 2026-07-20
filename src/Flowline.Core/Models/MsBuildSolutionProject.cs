namespace Flowline.Core.Models;

/// <summary>
/// A project entry in an MSBuild solution file (<c>.sln</c> or <c>.slnx</c>).
/// </summary>
/// <remarks>
/// "Solution" here means the MSBuild/Visual Studio solution file, not a Dataverse solution.
/// Everywhere else in Flowline the unqualified word means the Dataverse artifact — see
/// <see cref="Flowline.Core.Services.SolutionReader"/>, which reads Dataverse solution records.
/// </remarks>
/// <param name="Path">
/// Path to the project, relative to the folder holding the solution file, using
/// <see cref="System.IO.Path.DirectorySeparatorChar"/>. Normalized by
/// <see cref="Flowline.Core.Services.MsBuildSolutionReader"/> so callers never see the raw
/// separator difference between the two formats.
/// </param>
/// <param name="Name">The project's display name as recorded in the solution file.</param>
/// <param name="Extension">The project file extension, lower-cased and including the dot (e.g. <c>.csproj</c>).</param>
public sealed record MsBuildSolutionProject(string Path, string Name, string Extension)
{
    /// <summary>True when this entry is a Dataverse solution package project (<c>.cdsproj</c>).</summary>
    public bool IsCdsProject => Extension == ".cdsproj";

    /// <summary>True when this entry is a C# project (<c>.csproj</c>).</summary>
    public bool IsCsProject => Extension == ".csproj";
}
