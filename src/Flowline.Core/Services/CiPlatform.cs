namespace Flowline.Core.Services;

/// <summary>
/// Names the CI system this process is running under, for logs and telemetry.
/// </summary>
/// <remarks>
/// This never gates a prompt — interactivity is Spectre's <c>Capabilities.Interactive</c>, which its own
/// CI profile enrichers already drive. This exists only because Spectre exposes a capability, not a
/// platform identity, and callers need to name the platform.
/// </remarks>
public static class CiPlatform
{
    /// <summary>The CI platform's short name, <c>"unknown"</c> for an unrecognized one that still sets
    /// <c>CI</c>, or <c>null</c> when not running under CI at all.</summary>
    public static string? Detect()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null) return "github";
        if (Environment.GetEnvironmentVariable("TF_BUILD") != null) return "azuredevops";
        if (Environment.GetEnvironmentVariable("JENKINS_URL") != null) return "jenkins";
        if (Environment.GetEnvironmentVariable("TEAMCITY_VERSION") != null) return "teamcity";
        if (Environment.GetEnvironmentVariable("CI") != null) return "unknown";
        return null;
    }
}
