using Flowline.Config;
using FluentAssertions;

namespace Flowline.Tests;

public class ProjectConfigTests
{
    [Fact]
    public void GetOrUpdateSolution_NewSolution_ReturnsWithName()
    {
        var config = new ProjectConfig();

        var sln = config.GetOrUpdateSolution("MySolution");

        sln!.Name.Should().Be("MySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_ExistingSolution_ReturnsSameSolution()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution");

        var sln = config.GetOrUpdateSolution("MySolution");

        sln!.Name.Should().Be("MySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_SingleSolution_ReturnsIt()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("OnlySolution");

        var sln = config.GetOrUpdateSolution(null);

        sln!.Name.Should().Be("OnlySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_MultipleSolutions_ReturnsNull()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("SolutionA");
        config.AddOrUpdateSolution("SolutionB");

        var sln = config.GetOrUpdateSolution(null);

        sln.Should().BeNull();
    }
}
