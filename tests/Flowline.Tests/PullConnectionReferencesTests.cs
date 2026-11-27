using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.ConnectionReferences;
using Flowline.Core.Environments;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class PullConnectionReferencesTests
{
    const string Dataverse = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps";

    static ConnectionReference Ref(string logical, string display = "", string? conn = "c1", int flows = 1,
        bool withPerson = false) =>
        new(Guid.NewGuid(), logical, display.Length > 0 ? display : logical, Dataverse, conn, new DateTime(2025, 1, 1), 10132,
            new Dictionary<EnvironmentRole, RoleBinding>(),
            Enumerable.Range(0, flows).Select(_ => Guid.NewGuid()).ToList(),
            withPerson ? [new Dependent(Guid.NewGuid(), 300)] : [], false);

    static Classification Empty() => new([], [], [], [], []);

    // -- mode --

    [Theory]
    [InlineData(false, true, "Advise")]
    [InlineData(false, false, "Advise")]
    [InlineData(true, true, "Ask")]
    [InlineData(true, false, "Act")]
    public void DecideMode_FlagAndTerminal(bool tidy, bool interactive, string expected) =>
        PullConnectionReferences.DecideMode(tidy, interactive).ToString().Should().Be(expected);

    [Fact]
    public void Settings_TidyFlag_DefaultsOffAndBinds()
    {
        new PullCommand.Settings().TidyConnRefs.Should().BeFalse();

        PullCommand.Settings? captured = null;
        var app = new CommandApp();
        app.Configure(c => c.AddCommand<CapturingCommand>("pull"));
        CapturingCommand.OnExecute = s => captured = s;

        app.Run(["pull", "--tidy-connrefs"]).Should().Be(0);
        captured!.TidyConnRefs.Should().BeTrue();
    }

    [Fact]
    public void Settings_ForceAll_DoesNotTurnOnTheTidy()
    {
        var settings = new PullCommand.Settings { Force = ["all"] };
        settings.TidyConnRefs.Should().BeFalse();
        PullCommand.ValidSpecifiers.Should().BeEquivalentTo(["dirty", "config", "all"]);
    }

    sealed class CapturingCommand : Command<PullCommand.Settings>
    {
        public static Action<PullCommand.Settings>? OnExecute;
        public override int Execute(CommandContext context, PullCommand.Settings settings, CancellationToken cancellationToken)
        {
            OnExecute?.Invoke(settings);
            return 0;
        }
    }

    // -- advice --

    [Fact]
    public void Advice_CountsEachKindAndNamesTheFlag()
    {
        var a = Ref("new_a");
        var b = Ref("new_b");
        var c = Ref("new_c", conn: "c2");
        var unused = Ref("new_unused", flows: 0);
        var generated = Ref("new_sharedcommondataserviceforapps_12864");
        var classification = new Classification(
            [new SameConnectionGroup(Dataverse, a, [b], b.FlowIds, [])],
            [new DifferentAccountGroup(Dataverse, [a, c])],
            [], [unused], [generated]);

        var lines = PullConnectionReferences.AdviceLines(classification);

        lines.Should().Equal(
            "Connection references in DEV: 1 duplicate group on one connection, 1 group on different accounts, 1 unused, 1 with a default name.",
            "Run 'flowline pull --tidy-connrefs' to untangle them.");
    }

    [Fact]
    public void Advice_PluralsAndSkipsZeroKinds()
    {
        var classification = new Classification([], [], [], [Ref("new_x", flows: 0), Ref("new_y", flows: 0)], []);

        PullConnectionReferences.AdviceLines(classification)[0]
            .Should().Be("Connection references in DEV: 2 unused.");
    }

    [Fact]
    public void Advice_NothingActionable_NoLines()
    {
        var held = new NotMergedGroup(Dataverse, [Ref("new_a"), Ref("new_b")], "A suspended flow uses one of them.");

        PullConnectionReferences.AdviceLines(Empty() with { NotMerged = [held] }).Should().BeEmpty();
        PullConnectionReferences.AdviceLines(Empty()).Should().BeEmpty();
    }

    // -- unattended with the flag --

    [Fact]
    public void UnattendedChoices_MergesFullEvidenceGroupsAndRemovesUnused_Only()
    {
        var a = Ref("new_a");
        var b = Ref("new_b");
        var c = Ref("new_c");
        var d = Ref("new_d");
        var full = new SameConnectionGroup(Dataverse, a, [b], b.FlowIds, []);
        var missing = new SameConnectionGroup(Dataverse, c, [d], d.FlowIds, [EnvironmentRole.Prod]);
        var unused = Ref("new_unused", flows: 0);
        var classification = new Classification(
            [full, missing], [new DifferentAccountGroup(Dataverse, [Ref("new_e"), Ref("new_f", conn: "c2")])],
            [], [unused], [Ref("new_sharedcommondataserviceforapps_12864")]);

        var choices = PullConnectionReferences.UnattendedChoices(classification);

        choices.MergeSame.Should().Equal(full);
        choices.MergeDifferent.Should().BeEmpty();
        choices.RemoveUnused.Should().Equal(unused);
        choices.Renames.Should().BeEmpty();
    }

    [Fact]
    public void MissingEvidenceLines_NameEachHeldGroupAndItsMissingRoles_Only()
    {
        var a = Ref("new_a");
        var b = Ref("new_b");
        var c = Ref("new_c");
        var d = Ref("new_d");
        var classification = Empty() with
        {
            SameConnection =
            [
                new SameConnectionGroup(Dataverse, a, [b], b.FlowIds, []),
                new SameConnectionGroup(Dataverse, c, [d], d.FlowIds, [EnvironmentRole.Test, EnvironmentRole.Prod]),
            ],
        };

        PullConnectionReferences.MissingEvidenceLines(classification).Should().Equal(
            "Not merging [bold]new_d[/] into [bold]new_c[/]: no settings file for Test, Prod, so they can't be compared there.");
    }

    // -- rename suggestion --

    [Theory]
    [InlineData("Microsoft Dataverse EmailSentiment-2869b", "svc-integration@contoso.com", "Microsoft Dataverse (svc-integration@contoso.com)")]
    [InlineData("Microsoft Dataverse EmailSentiment-2869b", null, "Microsoft Dataverse")]
    [InlineData("Microsoft Dataverse", "Remy", "Microsoft Dataverse (Remy)")]
    public void SuggestDisplayName_StripsGeneratedSuffixAndAddsTheConnectionLabel(string display, string? label, string expected) =>
        PullConnectionReferences.SuggestDisplayName(display, label).Should().Be(expected);

    // -- stop --

    [Fact]
    public void StopMessage_NamesDoneAndNotDoneItems()
    {
        IReadOnlyList<CleanupOutcome> outcomes =
        [
            new("flow", "Order sync", CleanupStatus.Done),
            new("flow", "Invoice mail", CleanupStatus.Failed, "it changed in DEV since the review"),
            new("reference", "new_b", CleanupStatus.NotAttempted),
        ];

        var ex = PullConnectionReferences.StopException(outcomes);

        ex.ExitCode.Should().Be(ExitCode.PartialSuccess);
        ex.Message.Should().Be(
            "Connection reference cleanup stopped at flow 'Invoice mail': it changed in DEV since the review. " +
            "Done: flow 'Order sync'. Not done: reference 'new_b'. Nothing was exported. Fix it, then re-run 'flowline pull --tidy-connrefs'.");
    }

    // -- binding lines --

    [Fact]
    public void BindingLines_NumberConnectionsPerEnvironment_AndNeverShowTheIds()
    {
        RoleBinding Bound(string id) => new(true, id);
        var a = Ref("new_a", conn: "shared-commondataser-aaaa") with
        {
            Bindings = new Dictionary<EnvironmentRole, RoleBinding> { [EnvironmentRole.Prod] = Bound("prod-svc-1") },
        };
        var b = Ref("new_b", conn: "shared-commondataser-aaaa") with
        {
            Bindings = new Dictionary<EnvironmentRole, RoleBinding> { [EnvironmentRole.Prod] = Bound("prod-svc-2") },
        };
        var c = Ref("new_c", conn: null);

        var lines = PullConnectionReferences.BindingLines([a, b, c]);

        lines.Should().Equal(
            "new_a  DEV: connection 1, Prod: connection 1",
            "new_b  DEV: connection 1, Prod: connection 2",
            "new_c  DEV: unbound, Prod: unbound");
        string.Join("\n", lines).Should().NotContain("shared-commondataser").And.NotContain("prod-svc");
    }

    // -- the run without and with the flag when the review cannot run --

    [Fact]
    public async Task Run_WithoutFlag_ReviewThatCannotRun_WarnsAndReturns()
    {
        var console = new TestConsole();

        await PullConnectionReferences.RunAsync(console, _ => throw new InvalidOperationException("no token"),
            "https://dev", "Contoso", Directory.CreateTempSubdirectory().FullName, tidy: false, interactive: false, CancellationToken.None);

        console.Output.Should().Contain("Couldn't check connection references: no token");
    }

    [Fact]
    public async Task Run_WithFlag_ReviewThatCannotRun_Stops()
    {
        var act = () => PullConnectionReferences.RunAsync(new TestConsole(), _ => throw new InvalidOperationException("no token"),
            "https://dev", "Contoso", Directory.CreateTempSubdirectory().FullName, tidy: true, interactive: false, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("no token");
    }
}
