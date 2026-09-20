using Flowline.Core.Console;
using FluentAssertions;
using Xunit;

namespace Flowline.Core.Tests.Console;

public class FlowlineStageTests
{
    [Theory]
    // The real spinner labels, with their markup left in.
    [InlineData("Packing [bold]AcmeBankCustomizations[/]...", "Packing")]
    [InlineData("Deploying [bold]AcmeBank[/] to [bold]Contoso PROD[/]...", "Deploying")]
    [InlineData("Checking [bold]https://acmebank.crm4.dynamics.com[/]...", "Checking")]
    [InlineData("Checking solutions in [bold]Contoso DEV[/]...", "Checking solutions in")]
    [InlineData("Reading this environment's connections...", "Reading this environment's connections")]
    [InlineData("Checking your setup...", "Checking your setup")]
    public void NameFrom_KeepsTheVerbPhraseAndDropsWhatItActsOn(string label, string expected) =>
        FlowlineStage.NameFrom(label).Should().Be(expected);

    [Theory]
    [InlineData("[bold]AcmeBank[/]...")]
    [InlineData("")]
    [InlineData("...")]
    public void NameFrom_AlwaysProducesANameEvenWhenThereIsNoVerbPhrase(string label) =>
        FlowlineStage.NameFrom(label).Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void NameFrom_NeverCarriesTheValueTheLabelInterpolated()
    {
        // A span's name is not one of its tags, so the scrubber never sees it. The name has to be
        // free of identifying values by construction.
        FlowlineStage.NameFrom("Looking up solution [bold]AcmeBankCustomizations[/]...")
            .Should().NotContain("AcmeBank");
    }
}

public class FlowlineStageActivityTests
{
    static System.Diagnostics.ActivityListener Listening(List<System.Diagnostics.Activity> stopped)
    {
        var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == "Flowline.CLI",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _)
                => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public async Task EverySpinnerPhaseProducesANamedStageSpan()
    {
        var stopped = new List<System.Diagnostics.Activity>();
        using var listener = Listening(stopped);
        var console = new Spectre.Console.Testing.TestConsole();

        await Spectre.Console.AnsiConsoleExtensions.Status(console).FlowlineSpinner().StartAsync("Packing [bold]AcmeBank[/]...", _ => Task.CompletedTask);

        // Matched by name rather than by being the only one: the listener is global to the source, so
        // a test running alongside this one contributes activities too.
        stopped.Should().Contain(a => a.DisplayName == "Packing");
    }

    [Fact]
    public async Task AFailingPhaseMarksItsStageFailedAndStillThrows()
    {
        var stopped = new List<System.Diagnostics.Activity>();
        using var listener = Listening(stopped);
        var console = new Spectre.Console.Testing.TestConsole();

        var act = async () => await Spectre.Console.AnsiConsoleExtensions.Status(console).FlowlineSpinner()
            .StartAsync("Importing [bold]AcmeBank[/]...", _ => throw new InvalidOperationException("import failed"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        var stage = stopped.Should().Contain(a => a.DisplayName == "Importing").Subject;
        stage.Status.Should().Be(System.Diagnostics.ActivityStatusCode.Error);
        stage.GetTagItem("stage.error").Should().Be(nameof(InvalidOperationException));
    }
}
