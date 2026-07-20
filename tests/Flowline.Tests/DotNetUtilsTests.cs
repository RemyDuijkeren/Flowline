// EnsureMapFilePathAsync was removed along with mapping support.
// BuildSolutionAsync itself is exercised through integration; the argument
// resolution it depends on is covered here without running a build.

using FluentAssertions;
using Flowline.Utils;

namespace Flowline.Tests;

public class DotNetUtilsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public DotNetUtilsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void BuildArguments_BothSlnAndSlnxPresent_NamesTheSlnx()
    {
        File.WriteAllText(Path.Combine(_root, "MySolution.sln"), "");
        File.WriteAllText(Path.Combine(_root, "MySolution.slnx"), "");

        var args = DotNetUtils.BuildArguments(_root, DotnetBuild.Debug, rebuild: false);

        // One named target, so MSBuild never has to choose between the two (MSB1011).
        args.Should().ContainSingle(a => a.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Which.Should().Be(Path.Combine(_root, "MySolution.slnx"));
        args.Should().NotContain(a => a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildArguments_OnlySlnPresent_NamesThatSln()
    {
        File.WriteAllText(Path.Combine(_root, "MySolution.sln"), "");

        var args = DotNetUtils.BuildArguments(_root, DotnetBuild.Debug, rebuild: false);

        args.Should().Equal("build", Path.Combine(_root, "MySolution.sln"), "--configuration", "Debug");
    }

    [Fact]
    public void BuildArguments_OnlySlnxPresent_NamesThatSlnx()
    {
        File.WriteAllText(Path.Combine(_root, "MySolution.slnx"), "");

        var args = DotNetUtils.BuildArguments(_root, DotnetBuild.Release, rebuild: false);

        args.Should().Equal("build", Path.Combine(_root, "MySolution.slnx"), "--configuration", "Release");
    }

    [Fact]
    public void BuildArguments_NoSolutionFile_LeavesTheProjectFolderBuildUnchanged()
    {
        File.WriteAllText(Path.Combine(_root, "Plugins.csproj"), "");

        var args = DotNetUtils.BuildArguments(_root, DotnetBuild.Release, rebuild: false);

        args.Should().Equal("build", "--configuration", "Release");
    }

    [Fact]
    public void BuildArguments_Rebuild_AppendsRebuildTarget()
    {
        var args = DotNetUtils.BuildArguments(_root, DotnetBuild.Release, rebuild: true);

        args.Should().Equal("build", "--configuration", "Release", "-t:Rebuild");
    }

    [Fact]
    public void BuildArguments_MissingFolder_LeavesArgumentsUnchanged()
    {
        var args = DotNetUtils.BuildArguments(Path.Combine(_root, "does-not-exist"), DotnetBuild.Debug, rebuild: false);

        args.Should().Equal("build", "--configuration", "Debug");
    }
}
