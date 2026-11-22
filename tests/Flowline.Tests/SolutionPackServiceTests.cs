using FluentAssertions;
using Flowline.Services;

namespace Flowline.Tests;

public class SolutionPackServiceTests
{
    [Fact]
    public void BuildZipFileName_VersionedUnmanagedName_WhenUnmanaged()
    {
        SolutionPackService.BuildZipFileName("Contoso", "1.2.0.0", managed: false)
            .Should().Be("Contoso_1_2_0_0_unmanaged.zip");
    }

    [Fact]
    public void BuildZipFileName_VersionedManagedName_WhenManaged()
    {
        SolutionPackService.BuildZipFileName("SpotlerConnector", "3.1.1.3", managed: true)
            .Should().Be("SpotlerConnector_3_1_1_3_managed.zip");
    }

    [Fact]
    public void ResolveZipPath_DefaultsToArtifactsNextToSolution_WhenNoOutputGiven()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");

        SolutionPackService.ResolveZipPath(root, output: null, "Contoso", "1.0.0.0", managed: false)
            .Should().Be(Path.Combine(root, "artifacts", "Contoso_1_0_0_0_unmanaged.zip"));
    }

    [Fact]
    public void ResolveZipPath_UsesOutputFolder_WhenGiven()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var output = Path.Combine(Path.GetTempPath(), "drop");

        SolutionPackService.ResolveZipPath(root, output, "Contoso", "1.0.0.0", managed: true)
            .Should().Be(Path.Combine(output, "Contoso_1_0_0_0_managed.zip"));
    }

    [Fact]
    public void ResolveZipPath_ResolvesRelativeOutputAgainstCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");

        SolutionPackService.ResolveZipPath(root, "drop", "Contoso", "1.0.0.0", managed: false)
            .Should().Be(Path.Combine(Path.GetFullPath("drop"), "Contoso_1_0_0_0_unmanaged.zip"));
    }
}

public class SolutionPackServiceGuardMessageTests
{
    [Theory]
    [InlineData("pack")]
    [InlineData("deploy")]
    public void BuildDirtyTreeMessage_NamesTheCommand(string command)
    {
        SolutionPackService.BuildDirtyTreeMessage(["Solution/src/Other/Solution.xml"], command)
            .Should().Be($"Uncommitted changes in Solution/src/Other/Solution.xml — commit or stash first, then {command}.");
    }

    [Fact]
    public void BuildDirtyTreeMessage_ShowsThreePathsAndACount_WhenMoreChanged()
    {
        SolutionPackService.BuildDirtyTreeMessage(["a", "b", "c", "d", "e"], "pack")
            .Should().Be("Uncommitted changes in a, b, c (+2 more) — commit or stash first, then pack.");
    }

    [Fact]
    public void BuildDriftMessage_ForPack_SaysTheZipWouldHoldOlderCopies()
    {
        SolutionPackService.BuildDriftMessage("pack")
            .Should().Be("Local changes not in Dataverse — the zip would hold the older committed copies. Run 'push' then 'pull' to capture them, or use --force drift to skip.");
    }

    [Fact]
    public void BuildDriftMessage_ForDeploy_KeepsTheRevertWarning()
    {
        SolutionPackService.BuildDriftMessage("deploy")
            .Should().Be("Local changes not in Dataverse — deploy would revert them. Run 'push' then 'pull' to capture them, or use --force drift to skip.");
    }
}

public class SolutionPackServiceBuildOutputTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"PackBuildOutput_{Guid.NewGuid():N}");

    public SolutionPackServiceBuildOutputTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void EnsureEveryOutputHasASlot_FailsWithPushPullFix_WhenBuildOutputHasNoSlot()
    {
        var match = new Flowline.Core.Deploy.SlotMatchResult([], ["Contoso.NewPlugins"], []);

        var act = () => SolutionPackService.EnsureEveryOutputHasASlot(match);

        act.Should().Throw<Flowline.Core.FlowlineException>()
            .Where(e => e.ExitCode == Flowline.Core.ExitCode.ValidationFailed
                        && e.Message.Contains("Contoso.NewPlugins")
                        && e.Message.Contains("'push' then 'pull'"));
    }

    [Fact]
    public void EnsureEveryOutputHasASlot_FailsNamingTheSlot_WhenSlotHasNoBuildOutput()
    {
        var slot = new Flowline.Core.Deploy.SolutionSlot(Flowline.Core.Deploy.SlotKind.WebResource, "cr_Sales/logo.svg", "WebResources/cr_Sales/logo.svg");
        var match = new Flowline.Core.Deploy.SlotMatchResult([], [], [slot]);

        var act = () => SolutionPackService.EnsureEveryOutputHasASlot(match);

        act.Should().Throw<Flowline.Core.FlowlineException>()
            .Where(e => e.ExitCode == Flowline.Core.ExitCode.ValidationFailed && e.Message.Contains("cr_Sales/logo.svg"));
    }

    [Fact]
    public void OverlayBuildOutput_ReplacesStagedSlotAndLeavesSourceUntouched()
    {
        var source = Path.Combine(_root, "Solution", "src", "PluginAssemblies", "ContosoPlugins-1", "ContosoPlugins.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "committed");
        var staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(Path.Combine(staged, "PluginAssemblies", "ContosoPlugins-1"));
        File.Copy(source, Path.Combine(staged, "PluginAssemblies", "ContosoPlugins-1", "ContosoPlugins.dll"));
        var built = Path.Combine(_root, "bin", "Contoso.Plugins.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllText(built, "fresh build");

        SolutionPackService.OverlayBuildOutput(staged, [new Flowline.Core.Deploy.SlotCopy(built, "PluginAssemblies/ContosoPlugins-1/ContosoPlugins.dll")]);

        File.ReadAllText(Path.Combine(staged, "PluginAssemblies", "ContosoPlugins-1", "ContosoPlugins.dll")).Should().Be("fresh build");
        File.ReadAllText(source).Should().Be("committed");
    }

    [Fact]
    public void OverlayBuildOutput_CreatesASlotBinaryThatIsNotCommitted()
    {
        var staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(staged);
        var built = Path.Combine(_root, "dist", "main.js");
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllText(built, "js");

        SolutionPackService.OverlayBuildOutput(staged, [new Flowline.Core.Deploy.SlotCopy(built, "WebResources/cr_Sales/main.js")]);

        File.ReadAllText(Path.Combine(staged, "WebResources", "cr_Sales", "main.js")).Should().Be("js");
    }

    [Fact]
    public void GitignoreAdvice_CarriesTheBlockAndWhatAdoptingItMeans()
    {
        var advice = SolutionPackService.GitignoreAdvice;

        advice.Should().Contain("Solution/src/PluginAssemblies/**/*.dll");
        advice.Should().Contain("!Solution/src/WebResources/**/*.data.xml");
        advice.Should().Contain("--binaries-from-build");
    }
}
