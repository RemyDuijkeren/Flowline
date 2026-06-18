using FluentAssertions;
using Flowline.Generators;

namespace Flowline.Tests.Generators;

public class PacGeneratorTests
{
    const string Namespace = "MySolution.Models";
    const string TempOutputPath = @"C:\solutions\MySolution\Plugins\Models~";

    // ── Always-present flags ─────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_AlwaysContains_SuppressINotifyPattern()
    {
        var args = PacGenerator.BuildArgs([], [], [], Namespace, TempOutputPath);

        args.Should().Contain("--suppressINotifyPattern");
    }

    [Fact]
    public void BuildArgs_AlwaysContains_Sgca()
    {
        var args = PacGenerator.BuildArgs([], [], [], Namespace, TempOutputPath);

        args.Should().Contain("-sgca");
    }

    [Fact]
    public void BuildArgs_AlwaysContains_EmitFieldsClasses()
    {
        var args = PacGenerator.BuildArgs([], [], [], Namespace, TempOutputPath);

        args.Should().Contain("--emitfieldsclasses");
    }

    // ── Entity filter (-enf) ─────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_EntityFilter_ContainsSolutionEntities()
    {
        var args = PacGenerator.BuildArgs(["account", "contact"], [], [], Namespace, TempOutputPath);

        var enfIndex = Array.IndexOf(args, "-enf");
        enfIndex.Should().BeGreaterThanOrEqualTo(0);
        args[enfIndex + 1].Should().Contain("account");
        args[enfIndex + 1].Should().Contain("contact");
    }

    [Fact]
    public void BuildArgs_EntityFilter_DeduplicatesExtraTablesAlreadyInSolution()
    {
        var solutionEntities = new[] { "account", "contact" };
        var extraTables = new[] { "account", "task" }; // "account" duplicated

        var args = PacGenerator.BuildArgs(solutionEntities, extraTables, [], Namespace, TempOutputPath);

        var enfIndex = Array.IndexOf(args, "-enf");
        var entityFilterValue = args[enfIndex + 1];
        var entities = entityFilterValue.Split(';');

        entities.Should().ContainSingle(e => e.Equals("account", StringComparison.OrdinalIgnoreCase));
        entities.Should().Contain("contact");
        entities.Should().Contain("task");
    }

    [Fact]
    public void BuildArgs_EntityFilter_DeduplicationIsCaseInsensitive()
    {
        var solutionEntities = new[] { "Account" };
        var extraTables = new[] { "account" }; // same entity, different case

        var args = PacGenerator.BuildArgs(solutionEntities, extraTables, [], Namespace, TempOutputPath);

        var enfIndex = Array.IndexOf(args, "-enf");
        var entities = args[enfIndex + 1].Split(';');

        entities.Should().HaveCount(1);
    }

    [Fact]
    public void BuildArgs_EntityFilter_IsSortedAlphabetically()
    {
        var solutionEntities = new[] { "task", "account" };
        var extraTables = new[] { "contact" };

        var args = PacGenerator.BuildArgs(solutionEntities, extraTables, [], Namespace, TempOutputPath);

        var enfIndex = Array.IndexOf(args, "-enf");
        var entities = args[enfIndex + 1].Split(';');

        entities.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    // ── Custom API args ──────────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_CustomApis_AbsentWhenNoneDiscovered()
    {
        var args = PacGenerator.BuildArgs(["account"], [], [], Namespace, TempOutputPath);

        args.Should().NotContain("--generatesdkmessages");
        args.Should().NotContain("--messagenamesfilter");
    }

    [Fact]
    public void BuildArgs_CustomApis_PresentWhenDiscovered()
    {
        var customApis = new[] { "my_CustomAction", "my_AnotherAction" }.AsReadOnly();

        var args = PacGenerator.BuildArgs(["account"], [], customApis, Namespace, TempOutputPath);

        args.Should().Contain("--generatesdkmessages");
        args.Should().Contain("--messagenamesfilter");
    }

    [Fact]
    public void BuildArgs_CustomApis_MessageNamesFilterContainsAllApis()
    {
        var customApis = new[] { "my_First", "my_Second" }.AsReadOnly();

        var args = PacGenerator.BuildArgs(["account"], [], customApis, Namespace, TempOutputPath);

        var filterIndex = Array.IndexOf(args, "--messagenamesfilter");
        args[filterIndex + 1].Should().Contain("my_First");
        args[filterIndex + 1].Should().Contain("my_Second");
    }

    // ── Output path and namespace ────────────────────────────────────────────

    [Fact]
    public void BuildArgs_OutputPath_IsPassedAfterDashO()
    {
        var args = PacGenerator.BuildArgs([], [], [], Namespace, TempOutputPath);

        var oIndex = Array.IndexOf(args, "-o");
        oIndex.Should().BeGreaterThanOrEqualTo(0);
        args[oIndex + 1].Should().Be(TempOutputPath);
    }

    [Fact]
    public void BuildArgs_Namespace_IsPassedAfterDashN()
    {
        var args = PacGenerator.BuildArgs([], [], [], Namespace, TempOutputPath);

        var nIndex = Array.IndexOf(args, "-n");
        nIndex.Should().BeGreaterThanOrEqualTo(0);
        args[nIndex + 1].Should().Be(Namespace);
    }

    // ── Command prefix ───────────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_StartsWithModelbuilderBuild()
    {
        var args = PacGenerator.BuildArgs([], [], [], Namespace, TempOutputPath);

        args[0].Should().Be("modelbuilder");
        args[1].Should().Be("build");
    }
}
