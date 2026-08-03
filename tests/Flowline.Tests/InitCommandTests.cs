using System.ComponentModel;
using System.Reflection;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class InitCommandTests
{
    // ── Standalone/greenfield wiring (mirrors SlnAddCommandTests' pattern for the same overrides) ──

    [Fact]
    public void RequiresProject_IsFalse_SoTheCommandRunsBeforeAProjectExists()
    {
        // init is how a Flowline project comes to exist (like clone) — losing this override would make
        // a fresh `flowline init` fail with "No Flowline project found" before it ever ran.
        var requiresProject = typeof(InitCommand).GetProperty("RequiresProject",
            BindingFlags.Instance | BindingFlags.NonPublic);

        requiresProject!.DeclaringType.Should().Be(typeof(InitCommand));
    }

    [Fact]
    public void ValidForceSpecifiers_IsDeclaredOnInitCommand()
    {
        var validForceSpecifiers = typeof(InitCommand).GetProperty("ValidForceSpecifiers",
            BindingFlags.Instance | BindingFlags.NonPublic);

        validForceSpecifiers!.DeclaringType.Should().Be(typeof(InitCommand));
    }

    // ── R1/R5/R6: settings surface — long-form-only flags, no short aliases (KD4) ──

    [Fact]
    public void Settings_OptionFlags_HaveNoShortAliases()
    {
        // "-v|--verbose"-shaped templates carry a short alias before the pipe; init's own flags
        // (--dev is shared with clone/generate, which also has none) must all be long-form only.
        var ownProperties = typeof(InitCommand.Settings).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.DeclaringType == typeof(InitCommand.Settings));

        foreach (var property in ownProperties)
        {
            var option = property.GetCustomAttribute<CommandOptionAttribute>();
            if (option is null) continue;

            option.ShortNames.Should().BeEmpty($"{property.Name}'s CommandOption should be long-form only, no short alias");
        }
    }

    sealed class CapturingInitSettingsCommand : Command<InitCommand.Settings>
    {
        public static InitCommand.Settings? Captured;

        protected override int Execute(CommandContext context, InitCommand.Settings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    static CommandApp BuildInitParseProbe()
    {
        var app = new CommandApp();
        app.Configure(c => c.AddCommand<CapturingInitSettingsCommand>("init"));
        return app;
    }

    // ── AE1: full flags bind cleanly (no prompts needed downstream) ──

    [Fact]
    public void CommandApp_FullFlags_BindAllSettings()
    {
        var exitCode = BuildInitParseProbe().Run([
            "init", "MySolution",
            "--dev", "https://contoso-dev.crm4.dynamics.com",
            "--display-name", "My Solution",
            "--publisher-prefix", "acme",
            "--publisher-name", "Acme Corp"
        ]);

        exitCode.Should().Be(0);
        var captured = CapturingInitSettingsCommand.Captured!;
        captured.Name.Should().Be("MySolution");
        captured.DevUrl.Should().Be("https://contoso-dev.crm4.dynamics.com");
        captured.DisplayName.Should().Be("My Solution");
        captured.PublisherPrefix.Should().Be("acme");
        captured.PublisherName.Should().Be("Acme Corp");
    }

    [Fact]
    public void CommandApp_NameOnly_LeavesOptionalFlagsNull()
    {
        var exitCode = BuildInitParseProbe().Run(["init", "MySolution"]);

        exitCode.Should().Be(0);
        var captured = CapturingInitSettingsCommand.Captured!;
        captured.Name.Should().Be("MySolution");
        captured.DevUrl.Should().BeNull();
        captured.DisplayName.Should().BeNull();
        captured.PublisherPrefix.Should().BeNull();
        captured.PublisherName.Should().BeNull();
    }

    // Same trap clone's positional fell into: a REQUIRED "<name>" makes Spectre reject a bare
    // `flowline init` at parse time, before ExecuteFlowlineAsync (and its name prompt) ever runs.
    // Goes through real Spectre binding, which the ResolveNameAsync unit tests below bypass.
    [Fact]
    public void CommandApp_NoNameArg_BindsWithNullName()
    {
        var exitCode = BuildInitParseProbe().Run(["init"]);

        exitCode.Should().Be(0);
        CapturingInitSettingsCommand.Captured!.Name.Should().BeNull();
    }

    // ── Optional name: prompted interactively, refused with the argument named when there's no TTY ──

    [Fact]
    public async Task ResolveName_NameGiven_ReturnsItWithoutPrompting()
    {
        var (command, _) = MakeInitCommand();
        // No prompt input queued on the TestConsole — prompting would throw instead of returning.

        var name = await command.ResolveNameAsync("MySolution", CancellationToken.None);

        name.Should().Be("MySolution");
    }

    [Fact]
    public async Task ResolveName_NoName_NonInteractive_ThrowsNamingTheArgument()
    {
        var (command, _) = MakeInitCommand(interactive: false);

        var act = () => command.ResolveNameAsync(null, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("init <name>");
    }

    [Fact]
    public async Task ResolveName_NoName_Interactive_PromptsForIt()
    {
        var (command, console) = MakeInitCommand();
        console.Input.PushTextWithEnter("PromptedSolution");

        var name = await command.ResolveNameAsync(null, CancellationToken.None);

        name.Should().Be("PromptedSolution");
    }

    // Interactivity comes from the injected console's capabilities — most tests want a TTY, so this
    // defaults to interactive and the no-TTY tests opt out.
    static (InitCommand Command, TestConsole Console) MakeInitCommand(bool interactive = true)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = interactive;
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions());
        var capture = new SubprocessCapture(console);
        var createSolutionService = new CreateSolutionService(console, capture);
        var createEnvironmentResolver = new CreateEnvironmentResolver(console, profileResolutionService, capture);
        var solutionCreateFlow = new SolutionCreateFlow(console, profileResolutionService, connector, new SolutionCreateService(), createSolutionService);

        var command = new InitCommand(console, new FlowlineRuntimeOptions(), profileResolutionService,
            NullLoggerFactory.Instance, capture, createEnvironmentResolver, solutionCreateFlow);

        return (command, console);
    }

    // -- CommandApp parse seam, exit-code-only -- Spectre renders --help via AnsiConsole, not
    // Console.Out, so content isn't capturable via simple redirection (same limitation
    // PushCommandTests documents for parse errors). A clean exit proves init parses as a registered
    // command with this Settings shape; the flag names themselves are covered above.
    sealed class NoOpInitSettingsCommand : Command<InitCommand.Settings>
    {
        protected override int Execute(CommandContext context, InitCommand.Settings settings, CancellationToken cancellationToken) => 0;
    }

    [Fact]
    public void CommandApp_InitHelp_ExitsCleanly()
    {
        var app = new CommandApp();
        app.Configure(c => c.AddCommand<NoOpInitSettingsCommand>("init"));

        var exitCode = app.Run(["init", "--help"]);

        exitCode.Should().Be(0);
    }

    // ── init is registered before clone in Program.cs (KD1: discoverable front door) ──

    [Fact]
    public void ProgramCs_RegistersInitBeforeClone()
    {
        var programCs = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Flowline", "Program.cs"));

        var initIndex = programCs.IndexOf("AddCommand<InitCommand>(\"init\")", StringComparison.Ordinal);
        var cloneIndex = programCs.IndexOf("AddCommand<CloneCommand>(\"clone\")", StringComparison.Ordinal);

        initIndex.Should().BeGreaterThan(-1, "InitCommand must be registered under \"init\"");
        cloneIndex.Should().BeGreaterThan(-1, "CloneCommand must still be registered under \"clone\"");
        initIndex.Should().BeLessThan(cloneIndex, "init is the discoverable front door — it's registered before clone (KD1)");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Flowline.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find Flowline.slnx above the test assembly.");
    }
}
