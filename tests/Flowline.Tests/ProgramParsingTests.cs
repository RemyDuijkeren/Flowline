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

    // -- N6: a missing required argument points at the full command's --help --

    [Theory]
    [InlineData(new[] { "deploy" }, "Run 'flowline deploy --help' for usage and examples.")]
    [InlineData(new[] { "settings", "set", "--value", "x" }, "Run 'flowline settings set --help' for usage and examples.")]
    public void MissingRequiredArgument_BuildsHelpPointerForTheFullCommand(string[] args, string expected)
    {
        // Pins Spectre's message text: if an upgrade rewords it, the pointer silently stops printing.
        var app = BuildRealApp(out _);

        var ex = Assert.ThrowsAny<CommandRuntimeException>(() => app.Run(args));

        CliConfiguration.BuildHelpPointer(args, ex.Message).Should().Be(expected);
    }

    [Theory]
    [InlineData(new[] { "pull", "--bump" }, "patch, minor, major, none")]
    [InlineData(new[] { "pull", "--bump", "--verbose" }, "patch, minor, major, none")]
    [InlineData(new[] { "generate", "--generator" }, "pac, xrmcontext3, xrmcontext, ebg")]
    public void OptionMissingItsValue_HintListsTheValuesAndPointsAtHelp(string[] args, string values)
    {
        // Pins Spectre's '__default_command' conversion message, which is what this shape produces.
        var app = BuildRealApp(out _);

        var ex = Assert.ThrowsAny<CommandRuntimeException>(() => app.Run(args));

        CliConfiguration.BuildMissingValueHint(ex.Message).Should()
            .Be($"An option is missing its value. It takes one of: {values}.");
        CliConfiguration.BuildHelpPointer(args, ex.Message).Should()
            .Be($"Run 'flowline {args[0]} --help' for usage and examples.");
    }

    [Fact]
    public void UnknownCommand_BuildsNoHelpPointer()
    {
        var app = BuildRealApp(out _);

        var ex = Assert.ThrowsAny<CommandRuntimeException>(() => app.Run(["pusj"]));

        CliConfiguration.BuildHelpPointer(["pusj"], ex.Message).Should().BeNull();
        ex.Pretty.Should().NotBeNull("the handler renders Spectre's suggestion from it");
    }

    // -- N3: commands with nothing to force don't declare --force, so the parser rejects it --

    [Theory]
    [InlineData("status")]
    [InlineData("diff")]
    [InlineData("scaffold", "plugins")]
    [InlineData("sln", "add", "x.csproj")]
    public void CommandWithoutForceSpecifiers_RejectsForceAsUnknownOption(params string[] command)
    {
        var app = BuildRealApp(out _);

        var act = () => app.Run([.. command, "--force", "all"]);

        act.Should().Throw<CommandParseException>().WithMessage("*force*");
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

    // -- push is a branch: bare push for everything, one subcommand per target --

    // The scope and path options are gone with no alias, and the target-only options exist only on
    // their target. All of them fail as unknown options, as the parser reports them.
    [Theory]
    [InlineData("push", "--scope", "plugins")]
    [InlineData("push", "--plugin-file", "x.dll")]
    [InlineData("push", "--webresources", "./dist")]
    [InlineData("push", "--assembly-only")]
    [InlineData("push", "--no-publish")]
    [InlineData("push", "plugins", "--no-publish")]
    [InlineData("push", "wr", "--assembly-only")]
    public void Push_OptionOnTheWrongCommand_FailsWithUnknownOptionError(params string[] args)
    {
        var app = BuildRealApp(out _);

        var act = () => app.Run(args);

        act.Should().Throw<CommandParseException>().WithMessage("*Unknown option*");
    }

    [Fact]
    public void PushHelp_ListsSharedOptionsBothSubcommandsAndTheirExamples()
    {
        var app = BuildRealApp(out var console);

        app.Run(["push", "--help"]);

        var output = console.Output;
        output.Should().Contain("plugins").And.Contain("webresources");
        output.Should().Contain("--dry-run").And.Contain("--no-build").And.Contain("--solution-name");
        output.Should().NotContain("--scope").And.NotContain("--plugin-file").And.NotContain("--no-publish");
        output.Should().Contain("flowline push plugins --assembly-only");
        output.Should().Contain("flowline push wr --path ./dist");
    }

    // Spectre lists only the subcommand's own options here, not the ones declared on the branch, so the
    // description points at 'push -h' for those.
    [Theory]
    [InlineData("plugins", "--assembly-only")]
    [InlineData("webresources", "--no-publish")]
    [InlineData("wr", "--no-publish")]
    public void PushSubcommandHelp_ListsItsOwnOptionsAndPointsAtTheShared(string subcommand, string ownOption)
    {
        var app = BuildRealApp(out var console);

        app.Run(["push", subcommand, "--help"]);

        console.Output.Should().Contain(ownOption).And.Contain("--path").And.Contain("'flowline push -h'");
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
