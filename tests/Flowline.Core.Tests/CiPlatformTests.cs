using Flowline.Core.Services;
using FluentAssertions;
using Xunit;

namespace Flowline.Core.Tests;

public class CiPlatformTests
{
    static readonly string[] s_ciVars = ["GITHUB_ACTIONS", "TF_BUILD", "JENKINS_URL", "TEAMCITY_VERSION", "CI"];

    // These are real process env vars, and GitHub Actions runners actually set them — a test asserting
    // the "not in CI" answer has to clear them, or it passes locally and fails in CI.
    static Dictionary<string, string?> SaveAndClearCiVars()
    {
        var saved = s_ciVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (var v in s_ciVars) Environment.SetEnvironmentVariable(v, null);
        return saved;
    }

    static void RestoreCiVars(Dictionary<string, string?> saved)
    {
        foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void Detect_ReturnsNull_WhenNoCiVarsSet()
    {
        var saved = SaveAndClearCiVars();
        try { CiPlatform.Detect().Should().BeNull(); }
        finally { RestoreCiVars(saved); }
    }

    [Theory]
    [InlineData("GITHUB_ACTIONS", "true", "github")]
    [InlineData("TF_BUILD", "True", "azuredevops")]
    [InlineData("JENKINS_URL", "http://jenkins.example.com", "jenkins")]
    [InlineData("TEAMCITY_VERSION", "2025.1", "teamcity")]
    [InlineData("CI", "true", "unknown")]
    public void Detect_ReturnsExpectedPlatform_ForKnownCiVar(string envVar, string envValue, string expected)
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable(envVar, envValue);
        try { CiPlatform.Detect().Should().Be(expected); }
        finally { RestoreCiVars(saved); }
    }
}
