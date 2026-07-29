using System.Reflection;

namespace Flowline.Utils;

/// <summary>
/// The version string shown to the user (<c>flowline --version</c>, welcome screen).
/// </summary>
/// <remarks>
/// Uses the NuGet package version rather than <c>AssemblyFileVersion</c>: MinVer stamps every
/// prerelease of the same release with an identical 4-part file version, so
/// <c>0.13.1-alpha.0.2</c> and <c>0.13.1-alpha.0.7</c> both report <c>0.13.1.0</c> — which makes it
/// impossible to tell whether a locally built tool actually replaced the installed one. The build
/// metadata suffix (<c>+&lt;sha&gt;</c>) is trimmed so this matches what <c>dotnet tool list -g</c>
/// prints and what <c>dotnet tool install --version</c> takes.
/// </remarks>
internal static class FlowlineVersion
{
    public static string Display { get; } = Resolve();

    static string Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0";

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
