using Flowline.Utils;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests;

public class ConsoleHelperTests
{
    [Fact]
    public void Confirm_NonInteractive_ForceContainsConfig_ReturnsTrueWithoutPrompting()
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var settings = new FlowlineSettings { Force = ["config"] };
            ConsoleHelper.Confirm("Overwrite it?", false, settings, "config").Should().BeTrue();
        }
        finally { RestoreCiVars(saved); }
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsAll_ReturnsTrueWithoutPrompting()
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var settings = new FlowlineSettings { Force = ["all"] };
            ConsoleHelper.Confirm("Overwrite it?", false, settings, "config").Should().BeTrue();
        }
        finally { RestoreCiVars(saved); }
    }

    [Fact]
    public void Confirm_NonInteractive_ForceEmpty_ThrowsForceRequiredNamingConfig()
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var settings = new FlowlineSettings { Force = [] };
            var act = () => ConsoleHelper.Confirm("Overwrite it?", false, settings, "config");
            act.Should().Throw<FlowlineException>()
                .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force config"));
        }
        finally { RestoreCiVars(saved); }
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsMatchingSpecifier_ReturnsTrueWithoutPrompting()
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var settings = new FlowlineSettings { Force = ["first-import"] };
            ConsoleHelper.Confirm("Continue?", false, settings, "first-import").Should().BeTrue();
        }
        finally { RestoreCiVars(saved); }
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsDifferentSpecifier_ThrowsNamingRequestedSpecifier()
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var settings = new FlowlineSettings { Force = ["config"] };
            var act = () => ConsoleHelper.Confirm("Continue?", false, settings, "first-import");
            act.Should().Throw<FlowlineException>()
                .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force first-import"));
        }
        finally { RestoreCiVars(saved); }
    }

    [Fact]
    public void IsInteractive_ShouldReturnFalse_WhenCiEnvVarIsSet()
    {
        // Arrange
        Environment.SetEnvironmentVariable("CI", "true");

        try
        {
            // Act
            bool result = ConsoleHelper.IsInteractive(null);

            // Assert
            result.Should().BeFalse();
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("CI", null);
        }
    }

    [Fact]
    public void IsInteractive_ShouldReturnFalse_WhenGithubActionsEnvVarIsSet()
    {
        // Arrange
        Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");

        try
        {
            // Act
            bool result = ConsoleHelper.IsInteractive(null);

            // Assert
            result.Should().BeFalse();
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
        }
    }

    [Fact]
    public void IsInteractive_ShouldReturnFalse_WhenTfBuildEnvVarIsSet()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TF_BUILD", "true");

        try
        {
            // Act
            bool result = ConsoleHelper.IsInteractive(null);

            // Assert
            result.Should().BeFalse();
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("TF_BUILD", null);
        }
    }

    static readonly string[] s_ciVars = ["GITHUB_ACTIONS", "TF_BUILD", "JENKINS_URL", "CI"];

    static Dictionary<string, string?> SaveAndClearCiVars()
    {
        var saved = s_ciVars.ToDictionary(v => v, v => Environment.GetEnvironmentVariable(v));
        foreach (var v in s_ciVars) Environment.SetEnvironmentVariable(v, null);
        return saved;
    }

    static void RestoreCiVars(Dictionary<string, string?> saved)
    {
        foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void DetectCIPlatform_ShouldReturnNull_WhenNoCiVarsSet()
    {
        var saved = SaveAndClearCiVars();
        try { ConsoleHelper.DetectCIPlatform().Should().BeNull(); }
        finally { RestoreCiVars(saved); }
    }

    [Theory]
    [InlineData("GITHUB_ACTIONS", "true", "github")]
    [InlineData("TF_BUILD", "True", "azuredevops")]
    [InlineData("JENKINS_URL", "http://jenkins.example.com", "jenkins")]
    [InlineData("CI", "true", "unknown")]
    public void DetectCIPlatform_ShouldReturnExpectedPlatform_ForKnownCiVar(string envVar, string envValue, string expected)
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable(envVar, envValue);
        try { ConsoleHelper.DetectCIPlatform().Should().Be(expected); }
        finally { RestoreCiVars(saved); }
    }
}
