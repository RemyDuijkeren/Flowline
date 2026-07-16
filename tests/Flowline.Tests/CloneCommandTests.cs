using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;

namespace Flowline.Tests;

public class CloneCommandTests
{
    // ── ResolveSolutionName / IsAlreadyCloned ──────────────────────────────
    // Guards clone --managed from silently updating config for a solution whose local
    // Package/src was never re-fetched for the new packagetype (see FindUnmanagedSourceAsync).
    // The guard only fires when: solution already has a config entry, the requested managed
    // value actually differs from it, and Package.cdsproj already exists on disk.

    [Fact]
    public void ResolveSolutionName_ExplicitName_ReturnsTrimmedName()
    {
        var config = new ProjectConfig();

        var result = CloneCommand.ResolveSolutionName(config, "  MySolution  ");

        result.Should().Be("MySolution");
    }

    [Fact]
    public void ResolveSolutionName_NoInputName_SingleSolution_ReturnsIt()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("OnlySolution");

        var result = CloneCommand.ResolveSolutionName(config, null);

        result.Should().Be("OnlySolution");
    }

    [Fact]
    public void ResolveSolutionName_NoInputName_MultipleSolutions_ReturnsNull()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("SolutionA");
        config.AddOrUpdateSolution("SolutionB");

        var result = CloneCommand.ResolveSolutionName(config, null);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveSolutionName_NoInputName_NoSolutions_ReturnsNull()
    {
        var config = new ProjectConfig();

        var result = CloneCommand.ResolveSolutionName(config, null);

        result.Should().BeNull();
    }

    [Fact]
    public void IsAlreadyCloned_NoCdsprojOnDisk_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var result = CloneCommand.IsAlreadyCloned(root, "MySolution");

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsAlreadyCloned_CdsprojExists_ReturnsTrue()
    {
        var root = CreateTempRoot();
        try
        {
            CreateFakeCdsproj(root, "MySolution");

            var result = CloneCommand.IsAlreadyCloned(root, "MySolution");

            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void CreateFakeCdsproj(string root, string solutionName)
    {
        var packageFolder = Path.Combine(root, "solutions", solutionName, "Package");
        Directory.CreateDirectory(packageFolder);
        File.WriteAllText(Path.Combine(packageFolder, "Package.cdsproj"), "<Project />");
    }

    // ── ValidateForce / HasForce ────────────────────────────────────────────

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingConfigAndAll()
    {
        var settings = new CloneCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new CloneCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_All_ApprovesConfig()
    {
        var settings = new CloneCommand.Settings { Force = ["all"] };

        settings.HasForce("config").Should().BeTrue();
    }
}
