namespace Flowline.Core.Services;

public static class CiEnvironment
{
    public static bool IsCi() =>
        Environment.GetEnvironmentVariable("CI") != null || // Most CI systems
        Environment.GetEnvironmentVariable("TF_BUILD") != null || // Azure DevOps
        Environment.GetEnvironmentVariable("JENKINS_URL") != null || // Jenkins
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null; // GitHub Actions
}
