using Flowline;
using FluentAssertions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

// R10/AE11: proves the actual production configuration (StrictParsing, root
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

    // -- R9/R10: provision's --prod is gone, replaced by --env (KTD1) --

    [Fact]
    public void Provision_OldProdSpelling_FailsWithUnknownOptionError()
    {
        var app = BuildRealApp(out _);

        var act = () => app.Run(["provision", "dev", "--prod", "https://contoso.crm4.dynamics.com"]);

        act.Should().Throw<CommandParseException>()
           .WithMessage("*prod*");
    }

    [Fact]
    public void Provision_EnvOption_Parses_ReachesCommandActivationRatherThanFailingToParse()
    {
        // BuildRealApp has no DI registrar, so a parsed command still fails — but at activation
        // (Spectre resolving ProvisionCommand's constructor dependencies), not at the parser. That
        // distinguishes it from the old --prod spelling above, which never gets this far.
        var app = BuildRealApp(out _);

        var act = () => app.Run(["provision", "dev", "--env", "https://contoso.crm4.dynamics.com"]);

        act.Should().Throw<CommandRuntimeException>()
           .WithMessage("*Could not resolve type*ProvisionCommand*");
    }

    // -- sync is no longer an alias of pull: the name is kept free for a later command --

    [Fact]
    public void Sync_IsNotACommand()
    {
        var app = BuildRealApp(out _);

        var act = () => app.Run(["sync"]);

        act.Should().Throw<CommandParseException>().WithMessage("*Unknown command 'sync'*");
    }

    // The settings verbs were renamed with no alias, so the old spellings must stop parsing rather than
    // quietly keep working.
    [Theory]
    [InlineData("push")]
    [InlineData("pull")]
    public void SettingsPushAndPull_AreNotCommands(string retired)
    {
        var app = BuildRealApp(out _);

        var act = () => app.Run(["settings", retired, "test"]);

        act.Should().Throw<CommandParseException>().WithMessage($"*Unknown command '{retired}'*");
    }

    [Fact]
    public void SettingsHelp_ListsApplyAndCapture_AndNotTheOldVerbs()
    {
        var app = BuildRealApp(out var console);

        app.Run(["settings", "--help"]);

        var output = console.Output;
        output.Should().Contain("apply").And.Contain("capture");
        output.Should().NotContain("settings push").And.NotContain("settings pull");
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("capture")]
    public void SettingsApplyAndCaptureHelp_ShowExamplesWithTheirOwnVerb(string verb)
    {
        var app = BuildRealApp(out var console);

        app.Run(["settings", verb, "--help"]);

        console.Output.Should().Contain($"flowline settings {verb} test");
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
