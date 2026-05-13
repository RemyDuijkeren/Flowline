using Flowline.Config;
using FluentAssertions;

namespace Flowline.Tests;

public class ProjectConfigTests
{
    [Fact]
    public void ProjectSolution_UseMapping_DefaultsToTrue()
    {
        new ProjectSolution { Name = "Test" }.UseMapping.Should().BeTrue();
    }

    [Fact]
    public void GetOrUpdateSolution_NewSolution_UseMappingNull_DefaultsTrue()
    {
        var config = new ProjectConfig();

        var sln = config.GetOrUpdateSolution("MySolution", useMapping: null);

        sln!.UseMapping.Should().BeTrue();
    }

    [Fact]
    public void GetOrUpdateSolution_NewSolution_UseMappingFalse_StoresFalse()
    {
        var config = new ProjectConfig();

        var sln = config.GetOrUpdateSolution("MySolution", useMapping: false);

        sln!.UseMapping.Should().BeFalse();
    }

    [Fact]
    public void GetOrUpdateSolution_ExistingSolution_UseMappingNull_PreservesStoredValue()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution", useMapping: false);

        var sln = config.GetOrUpdateSolution("MySolution", useMapping: null);

        sln!.UseMapping.Should().BeFalse();
    }

    [Fact]
    public void GetOrUpdateSolution_ExistingSolution_UseMappingMatches_NoChange()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution", useMapping: true);

        var sln = config.GetOrUpdateSolution("MySolution", useMapping: true);

        sln!.UseMapping.Should().BeTrue();
    }

    [Fact]
    public void GetOrUpdateSolution_ExistingSolution_UseMappingDiffers_Updates()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution", useMapping: true);

        var sln = config.GetOrUpdateSolution("MySolution", useMapping: false);

        sln!.UseMapping.Should().BeFalse();
    }

    [Fact]
    public void GetOrUpdateSolution_ExistingSolution_UseMappingRestored_Updates()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution", useMapping: false);

        var sln = config.GetOrUpdateSolution("MySolution", useMapping: true);

        sln!.UseMapping.Should().BeTrue();
    }
}
