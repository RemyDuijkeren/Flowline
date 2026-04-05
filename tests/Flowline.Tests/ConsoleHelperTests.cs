using Flowline.Utils;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests;

public class ConsoleHelperTests
{
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
}
