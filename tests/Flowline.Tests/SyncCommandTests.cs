using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class SyncCommandTests
{
    [Fact]
    public void Settings_Force_ShouldDefaultToEmpty()
    {
        new SyncCommand.Settings().Force.Should().BeEmpty();
    }

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingValidValues()
    {
        var settings = new SyncCommand.Settings { Force = ["delete-orphans"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, SyncCommand.ValidSpecifiers, "sync");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                && e.Message.Contains("dirty") && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_ValidValues_DoesNotThrow()
    {
        var settings = new SyncCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, SyncCommand.ValidSpecifiers, "sync");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_All_ApprovesDirtyAndConfigTogether()
    {
        var settings = new SyncCommand.Settings { Force = ["all"] };

        settings.HasForce("dirty").Should().BeTrue();
        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void HasForce_ConfigOnly_DoesNotApproveDirty()
    {
        var settings = new SyncCommand.Settings { Force = ["config"] };

        settings.HasForce("config").Should().BeTrue();
        settings.HasForce("dirty").Should().BeFalse();
    }

    [Fact]
    public void Settings_Bump_ShouldDefaultToPatch()
    {
        new SyncCommand.Settings().Bump.Should().Be(BumpComponent.Patch);
    }
}

/// <summary>
/// Sync's output shape — where CHANGES.md lands, its provenance line, and the terminal no-changes line —
/// is composed here and passed to a writer shared with `diff`. The call site that uses it needs a live
/// environment to reach, so these lock the values directly.
/// </summary>
public class SyncCommandOutputShapeTests
{
    [Fact]
    public void ChangesFilePath_IsChangesMdAtTheGivenRoot()
    {
        SyncCommand.ChangesFilePath(Path.Combine("C:", "repo"))
            .Should().Be(Path.Combine("C:", "repo", "CHANGES.md"));
    }

    [Fact]
    public void ProvenanceLine_NamesTheEnvironment()
    {
        SyncCommand.ProvenanceLine("Contoso Dev").Should().Be("Synced from: Contoso Dev");
    }

    [Fact]
    public void ProvenanceLine_WithoutEnvironment_IsOmittedEntirely()
    {
        SyncCommand.ProvenanceLine(null).Should().BeNull();
    }

    [Fact]
    public void NoChangesLine_NamesTheEnvironment()
    {
        SyncCommand.NoChangesLine("Contoso Dev").Should().Be("No changes pulled from Contoso Dev.");
    }

    [Fact]
    public void NoChangesLine_WithoutEnvironment_FallsBackToDev()
    {
        SyncCommand.NoChangesLine(null).Should().Be("No changes pulled from DEV.");
    }

    [Fact]
    public void NoChangesLine_EscapesMarkupInTheEnvironmentName()
    {
        // An environment display name is user data reaching a Spectre markup renderer.
        SyncCommand.NoChangesLine("Contoso [Dev]").Should().Be("No changes pulled from Contoso [[Dev]].");
    }
}

public class BumpVersionTests
{
    [Theory]
    [InlineData("1.0.0.1", BumpComponent.Patch, "1.0.1.0")]
    [InlineData("1.0.9.3", BumpComponent.Patch, "1.0.10.0")]
    [InlineData("1.2.5.3", BumpComponent.Minor, "1.3.0.0")]
    [InlineData("1.2.5.3", BumpComponent.Major, "2.0.0.0")]
    public void BumpVersion_ShouldIncrementCorrectComponent(string version, BumpComponent component, string expected)
    {
        SyncCommand.BumpVersion(version, component).Should().Be(expected);
    }

    [Fact]
    public void BumpVersion_None_ShouldThrow()
    {
        var act = () => SyncCommand.BumpVersion("1.0.0.1", BumpComponent.None);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("1.0.1.0", "1.0.1")]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("2.0.0.0", "2.0.0")]
    [InlineData("1.3.0.0", "1.3.0")]
    public void ToTagVersion_ShouldReturnThreePart(string version, string expected)
    {
        SyncCommand.ToTagVersion(version).Should().Be(expected);
    }
}

/// <summary>
/// The reporting tail sync runs once the summary exists: the terminal tree, CHANGES.md, and the
/// regenerated schema context. Extracted from the command so it can run against a temp folder — the
/// call site itself needs a live Dataverse environment to reach.
/// </summary>
public class SyncReportTests : IDisposable
{

    /// <summary>
    /// The overflow hint is caller-supplied so `diff` can stop naming a file it never wrote. Sync always
    /// writes CHANGES.md, so it must keep passing that route: dropping the argument is a silent text
    /// regression no other test would catch.
    /// </summary>
    [Fact]
    public async Task WriteSyncReportAsync_WithMoreSubChangesThanTheCap_StillPointsAtChangesMd()
    {
        var console = new TestConsole();
        var subs = Enumerable.Range(1, SolutionChangeSummary.SubChangeDisplayThreshold + 1)
            .Select(i => new SolutionChangeSummary.SubChange($"field{i}", SolutionChangeSummary.ChangeStatus.Added))
            .ToList();
        var summary = new SolutionChangeSummary(1, 6, 0, [
            new SolutionChangeSummary.ChangeGroup("Account", [
                new SolutionChangeSummary.ChangeItem("entity metadata", [], SolutionChangeSummary.ChangeStatus.Modified, subs)
            ])
        ]);

        await SyncCommand.WriteSyncReportAsync(summary, console, _root, Path.Combine(_root, "src"),
            "ContosoSolution", "Contoso Dev", verbose: false, CancellationToken.None);

        console.Output.Should().Contain("see CHANGES.md");
    }

    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-syncreport-" + Guid.NewGuid().ToString("N"));

    public SyncReportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    static SolutionChangeSummary WithChanges() =>
        new(1, 2, 0, [new SolutionChangeSummary.ChangeGroup("Entities", [
            new SolutionChangeSummary.ChangeItem("Account", ["src/Entities/Account/Entity.xml"])])]);

    static SolutionChangeSummary NoChanges() => new(0, 0, 0, []);

    Task Report(SolutionChangeSummary summary, TestConsole console, string? envDisplayName) =>
        SyncCommand.WriteSyncReportAsync(summary, console, _root, Path.Combine(_root, "Solution", "src"),
            "ContosoCustomizations", envDisplayName, verbose: false);

    [Fact]
    public async Task ChangesFile_LandsAtTheRoot_AndNamesTheEnvironment()
    {
        await Report(WithChanges(), new TestConsole(), "Contoso Dev");

        var changesFile = Path.Combine(_root, "CHANGES.md");
        File.Exists(changesFile).Should().BeTrue();
        (await File.ReadAllTextAsync(changesFile)).Should().Contain("Synced from: Contoso Dev");
    }

    [Fact]
    public async Task NoChanges_WritesNoChangesFileAtAll()
    {
        await Report(NoChanges(), new TestConsole(), "Contoso Dev");

        File.Exists(Path.Combine(_root, "CHANGES.md")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Contoso Dev", "No changes pulled from Contoso Dev.")]
    [InlineData(null, "No changes pulled from DEV.")]
    public async Task NoChanges_TerminalLineNamesTheEnvironment(string? envDisplayName, string expected)
    {
        var console = new TestConsole();

        await Report(NoChanges(), console, envDisplayName);

        console.Output.Should().Contain(expected);
    }

    [Fact]
    public async Task SchemaContextDocument_IsRegenerated()
    {
        await Report(WithChanges(), new TestConsole(), "Contoso Dev");

        File.Exists(Path.Combine(_root, "docs", "DATAVERSE_CONTEXT.md")).Should().BeTrue();
    }
}
