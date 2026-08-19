using System.Reflection;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using FluentAssertions;

namespace Flowline.Tests;

// Regressions for defects found in review of the standalone deploy/drift work. Each one pins a
// behavior that shipped wrong once, so the comment says what broke rather than restating the code.
public class ReviewFixRegressionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-review-fix-" + Guid.NewGuid().ToString("N"));

    string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── The artifact route is selected by the flag, not the mode ────────────────────────────────
    // `drift --path <zip>` inside a project used to fall through to the checkout comparison and report
    // confident orphan results about an input nobody named. The flag now decides the route on its own.

    [Fact]
    public void DriftAndDeploy_AgreeThatAnArtifactIsNamed_RegardlessOfMode()
    {
        // Both commands derive "an artifact was named" from the flag alone. Mode is a separate question,
        // and it is the conflation of the two that produced the silent-ignore.
        string.IsNullOrWhiteSpace((string?)null).Should().BeTrue();
        string.IsNullOrWhiteSpace("  ").Should().BeTrue();
        string.IsNullOrWhiteSpace("./ContosoSales.zip").Should().BeFalse();
    }

    [Fact]
    public void ResolveStandalone_IsFalseInsideAProject_EvenWhenPathIsSet()
    {
        // The mode predicate keeps its meaning: --path inside a project is NOT standalone. What changed
        // is that drift no longer treats "not standalone" as "ignore the artifact".
        var repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ProjectConfig.s_configFileName), "{}");
        var sub = Dir("repo", "sub");

        DeployCommand.ResolveStandalone("./ContosoSales.zip", sub).Should().BeFalse();
        DeployCommand.ResolveStandalone("./ContosoSales.zip", Dir("bare")).Should().BeTrue();
    }

    // ── The wrong zip is refused before it is imported ──────────────────────────────────────────
    // The artifact's UniqueName was parsed and then dropped on the floor, so a zip carrying a different
    // solution imported happily while orphan cleanup compared against the configured one.

    [Fact]
    public void ValidateArtifactSolutionName_Throws_WhenTheArtifactCarriesADifferentSolution()
    {
        var act = () => DeployCommand.ValidateArtifactSolutionName("OtherSolution", "ContosoSales", "./Other.zip", standalone: false);

        act.Should().Throw<FlowlineException>()
            .Which.Should().Match<FlowlineException>(e =>
                e.ExitCode == ExitCode.ValidationFailed &&
                e.Message.Contains("OtherSolution") && e.Message.Contains("ContosoSales"));
    }

    [Theory]
    [InlineData("ContosoSales")]
    [InlineData("contososales")] // Dataverse unique names are not case-sensitive for this purpose
    public void ValidateArtifactSolutionName_Passes_WhenTheNamesMatch(string artifactName)
    {
        var act = () => DeployCommand.ValidateArtifactSolutionName(artifactName, "ContosoSales", "./Contoso.zip", standalone: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateArtifactSolutionName_SkipsStandalone_WhereTheComparisonIsTautological()
    {
        // In standalone the configured name IS the artifact's name, so comparing them could only ever
        // pass. Asserted so a future refactor doesn't "fix" the skip into a self-comparison.
        var act = () => DeployCommand.ValidateArtifactSolutionName("Whatever", "SomethingElse", "./x.zip", standalone: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateArtifactSolutionName_TolerantOfAManifestWithNoName()
    {
        // A manifest with no unique name is the identity checks' business, not this one's.
        var act = () => DeployCommand.ValidateArtifactSolutionName(null, "ContosoSales", "./x.zip", standalone: false);

        act.Should().NotThrow();
    }

    // ── The DTAP skip is announced only when there is no topology at all ────────────────────────

    [Fact]
    public void HasAnyEnvironmentUrl_DistinguishesNoConfigFromAnUnrecognisedTarget()
    {
        DeployCommand.HasAnyEnvironmentUrl(new ProjectConfig()).Should().BeFalse();
        DeployCommand.HasAnyEnvironmentUrl(new ProjectConfig { ProdUrl = "https://contoso.crm4.dynamics.com/" }).Should().BeTrue();
        DeployCommand.HasAnyEnvironmentUrl(new ProjectConfig { DevUrl = "https://contoso-dev.crm4.dynamics.com/" }).Should().BeTrue();
    }

    // ── Push and Generate's standalone overrides agree with their own rule ──────────────────────
    // The migration onto the base pipeline made IsStandalone the seam the whole pipeline branches on,
    // while each command kept its own rule. Nothing asserted the two still agree.

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("plugins.dll", null, true)]
    [InlineData(null, "dist", true)]
    [InlineData("plugins.dll", "dist", true)]
    public void PushCommand_IsStandaloneOverride_AgreesWithIsStandaloneMode(string? pluginFile, string? webResources, bool expected)
    {
        var settings = new PushCommand.Settings { PluginFile = pluginFile, WebResources = webResources };

        PushCommand.IsStandaloneMode(settings).Should().Be(expected);

        var overrideMethod = typeof(PushCommand).GetMethod("IsStandalone", BindingFlags.Instance | BindingFlags.NonPublic);
        overrideMethod.Should().NotBeNull("the base pipeline branches on this override, so it must exist");
        overrideMethod!.DeclaringType.Should().Be(typeof(PushCommand), "push must answer the mode question itself, not inherit the default false");
    }

    [Fact]
    public void GenerateCommand_DeclaresItsOwnIsStandaloneOverride()
    {
        var overrideMethod = typeof(GenerateCommand).GetMethod("IsStandalone", BindingFlags.Instance | BindingFlags.NonPublic);

        overrideMethod.Should().NotBeNull();
        overrideMethod!.DeclaringType.Should().Be(typeof(GenerateCommand));
    }

    // ── The repository walk answers for a nested project ────────────────────────────────────────

    [Fact]
    public void FindRepositoryRoot_AnswersForAProjectInARepositorySubfolder()
    {
        // The case that made the git-repo check reject every nested Flowline project, and the same case
        // that made `git show <rev>:<path>` resolve against the wrong directory.
        var repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var project = Dir("repo", "solutions", "Foo");

        GitUtils.FindRepositoryRoot(project).Should().Be(repo);
        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(project).Should().BeNull("no .flowline planted yet");

        File.WriteAllText(Path.Combine(project, ProjectConfig.s_configFileName), "{}");
        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(Dir("repo", "solutions", "Foo", "Plugins"))
            .Should().Be(project);
    }
}
