using Flowline;
using FluentAssertions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

// R10/AE11: proves the actual production configuration (StrictParsing, the pull/sync alias, root
// examples) rather than a hand-written mirror that could drift from it — every app here is built
// through CliConfiguration.Configure, the same method Program.cs calls.
public class ProgramParsingTests
{
    static CommandApp BuildRealApp(out TestConsole console)
    {
        var app = new CommandApp();
        console = new TestConsole().Width(200); // wide enough that help text doesn't wrap mid-assertion
        var capturedConsole = console;
        app.Configure(config =>
        {
            config.SetApplicationName("flowline");
            config.Settings.Console = capturedConsole;
            config.PropagateExceptions();
            CliConfiguration.Configure(config);
        });
        return app;
    }

    // -- R10: strict parsing rejects an old/unknown option spelling --

    [Fact]
    public void Push_OldPluginFileSpelling_FailsWithUnknownOptionError()
    {
        var app = BuildRealApp(out _);

        var act = () => app.Run(["push", "--pluginFile", "x"]);

        act.Should().Throw<CommandParseException>()
           .WithMessage("*pluginFile*");
    }

    [Fact]
    public void Deploy_UnknownOption_FailsWithUnknownOptionError()
    {
        var app = BuildRealApp(out _);

        var act = () => app.Run(["deploy", "prod", "--bogus"]);

        act.Should().Throw<CommandParseException>()
           .WithMessage("*bogus*");
    }

    // -- AE11: sync is a permanent alias of pull, and help renders the primary name --

    [Fact]
    public void SyncHelp_RendersPullInTheHeaderAndUsageLine()
    {
        var app = BuildRealApp(out var console);

        app.Run(["sync", "--help"]);

        var output = console.Output;
        output.Should().Contain("flowline pull [OPTIONS]");
        output.Should().NotContain("flowline sync [OPTIONS]");
    }

    [Fact]
    public void PullHelp_RendersPullInTheUsageLine()
    {
        var app = BuildRealApp(out var console);

        app.Run(["pull", "--help"]);

        console.Output.Should().Contain("flowline pull [OPTIONS]");
    }

    // -- R23: root help shows exactly the four AE-required examples --

    [Fact]
    public void RootHelp_ShowsTheFourRequiredExamples()
    {
        var app = BuildRealApp(out var console);

        app.Run(["--help"]);

        var output = console.Output;
        output.Should().Contain("flowline clone ContosoSales");
        output.Should().Contain("flowline push");
        output.Should().Contain("flowline pull");
        output.Should().Contain("flowline deploy prod");
    }
}
