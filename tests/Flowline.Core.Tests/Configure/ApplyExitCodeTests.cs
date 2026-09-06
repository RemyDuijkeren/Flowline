using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Configure;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class ApplyExitCodeTests
{
    static ComponentOutcome Outcome(ComponentOutcomeKind kind, string name = "c") =>
        new(ConfigurableComponentKind.Flow, name, kind);

    static ApplyOutcome Result(params ComponentOutcome[] outcomes) => new(outcomes, []);

    [Fact]
    public void EverythingApplied_IsSuccess()
    {
        Result(Outcome(ComponentOutcomeKind.Applied)).ExitCode.Should().Be(ExitCode.Success);
    }

    // R6/AE1: a file re-applied to the environment it came from changes nothing and is a clean pass.
    [Fact]
    public void EverythingUnchanged_IsSuccess()
    {
        Result(Outcome(ComponentOutcomeKind.Unchanged), Outcome(ComponentOutcomeKind.Unchanged))
            .ExitCode.Should().Be(ExitCode.Success);
    }

    // KTD1: PartialSuccess covers some-failed and all-failed alike. The recovery is identical either way --
    // fix the cause and re-run -- so a separate code would buy no distinct action.
    [Fact]
    public void SomeFailed_IsPartialSuccess()
    {
        Result(Outcome(ComponentOutcomeKind.Applied), Outcome(ComponentOutcomeKind.Failed))
            .ExitCode.Should().Be(ExitCode.PartialSuccess);
    }

    [Fact]
    public void AllFailed_IsAlsoPartialSuccess()
    {
        Result(Outcome(ComponentOutcomeKind.Failed), Outcome(ComponentOutcomeKind.Failed))
            .ExitCode.Should().Be(ExitCode.PartialSuccess);
    }

    // AE3: nothing was compared, so the run is not a pass signal. This is the wrong-file or
    // wrong-environment case.
    [Fact]
    public void EverythingSkipped_IsInconclusive()
    {
        Result(Outcome(ComponentOutcomeKind.Skipped), Outcome(ComponentOutcomeKind.Skipped))
            .ExitCode.Should().Be(ExitCode.Inconclusive);
    }

    [Fact]
    public void SomeSkippedButOthersApplied_IsSuccess()
    {
        Result(Outcome(ComponentOutcomeKind.Applied), Outcome(ComponentOutcomeKind.Skipped))
            .ExitCode.Should().Be(ExitCode.Success);
    }

    // Precedence matches deploy's post-import resolution: a failure outranks an inconclusive result.
    [Fact]
    public void FailureOutranksInconclusive()
    {
        Result(Outcome(ComponentOutcomeKind.Skipped), Outcome(ComponentOutcomeKind.Failed))
            .ExitCode.Should().Be(ExitCode.PartialSuccess);
    }

    [Fact]
    public void EmptyFile_IsSuccess()
    {
        // Nothing declared is not the same as nothing matched: there was no comparison to be inconclusive about.
        Result().ExitCode.Should().Be(ExitCode.Success);
    }

    // R9: undeclared components are a warning and never move the exit code.
    [Fact]
    public void UndeclaredComponents_DoNotAffectTheExitCode()
    {
        var outcome = new ApplyOutcome([Outcome(ComponentOutcomeKind.Unchanged)], ["Flow: something_else"]);

        outcome.ExitCode.Should().Be(ExitCode.Success);
        outcome.Undeclared.Should().ContainSingle();
    }

    // R11a: the summary carries what the exit code drops -- one failed component and every component failing
    // share a code, and only the counts tell them apart.
    [Fact]
    public void SummaryLine_CarriesEveryCount()
    {
        var outcome = new ApplyOutcome(
            [
                Outcome(ComponentOutcomeKind.Applied),
                Outcome(ComponentOutcomeKind.Unchanged),
                Outcome(ComponentOutcomeKind.Skipped),
                Outcome(ComponentOutcomeKind.Failed),
            ],
            ["Flow: other"]);

        outcome.SummaryLine().Should().Be("1 applied, 1 unchanged, 1 skipped, 1 failed, 1 undeclared");
    }
}
