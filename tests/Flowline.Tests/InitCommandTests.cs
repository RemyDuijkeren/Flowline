using System.ComponentModel;
using System.Reflection;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class InitCommandTests
{
    // ── Standalone/greenfield wiring (mirrors SlnAddCommandTests' pattern for the same overrides) ──

    [Fact]
    public void RequiresFlowlineProject_IsFalse_SoTheCommandRunsBeforeAProjectExists()
    {
        // init is how a Flowline project comes to exist (like clone) — losing this override would make
        // a fresh `flowline init` fail with "No Flowline project found" before it ever ran.
        var requiresProject = typeof(InitCommand).GetProperty("RequiresFlowlineProject",
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
        var (command, _, _) = MakeInitCommand();
        // No prompt input queued on the TestConsole — prompting would throw instead of returning.

        var name = await command.ResolveNameAsync("MySolution", CancellationToken.None);

        name.Should().Be("MySolution");
    }

    [Fact]
    public async Task ResolveName_NoName_NonInteractive_ThrowsNamingTheArgument()
    {
        var (command, _, _) = MakeInitCommand(interactive: false);

        var act = () => command.ResolveNameAsync(null, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("init <name>");
    }

    [Fact]
    public async Task ResolveName_NoName_Interactive_PromptsForIt()
    {
        var (command, console, _) = MakeInitCommand();
        console.Input.PushTextWithEnter("PromptedSolution");

        var name = await command.ResolveNameAsync(null, CancellationToken.None);

        name.Should().Be("PromptedSolution");
    }

    // Interactivity comes from the injected console's capabilities — most tests want a TTY, so this
    // defaults to interactive and the no-TTY tests opt out.
    static (InitCommand Command, TestConsole Console, IOrganizationServiceAsync2 OrgService) MakeInitCommand(bool interactive = true)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = interactive;
        var connector = new DataverseConnector(console, new HttpClient());
        var profile = new PacProfile { Name = "Contoso", Resource = DevUrl };
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => new ProfileFound(profile),
            IsProfileActiveOverride = _ => true,
            // Without this, EnsureActiveProfileAsync falls through to the real
            // DataverseConnector.GetPacProfiles(), which reads authprofiles_v2.json off disk — present
            // on a dev machine, absent on a CI runner.
            GetPacProfilesOverride = () => [profile]
        };

        var orgService = Substitute.For<IOrganizationServiceAsync2>();
        // Default: no existing publisher, no existing solution -- individual tests override.
        orgService.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var capture = new SubprocessCapture(console);
        var projectScaffolder = new ProjectScaffolder(console, capture);
        var createEnvironmentResolver = new CreateEnvironmentResolver(console, profileResolutionService, capture);

        var command = new InitCommand(console, new FlowlineRuntimeOptions(), profileResolutionService,
            NullLoggerFactory.Instance, capture, createEnvironmentResolver, connector, new SolutionCreateService(), projectScaffolder, new NuGetVersionClient(new HttpClient()))
        {
            ConnectOverride = (_, _, _) => Task.FromResult(orgService),
            ValidatePackAndBuildOverride = s_succeedingBuild
        };

        return (command, console, orgService);
    }

    // ── The create sequence ───────────────────────────────────────────────────

    const string DevUrl = "https://contoso-dev.crm4.dynamics.com";

    static EnvironmentInfo MakeDevEnv() => new() { DisplayName = "Contoso Dev", EnvironmentUrl = DevUrl, Type = "Sandbox" };

    static readonly Func<ProjectSolution, string, string, CancellationToken, Task<int?>> s_succeedingBuild =
        (_, _, _, _) => Task.FromResult<int?>(null);

    static Func<ProjectSolution, string, string, CancellationToken, Task<int?>> FailingBuild(int exitCode) =>
        (_, _, _, _) => Task.FromResult<int?>(exitCode);

    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // Pre-seeds a root so the scaffold's pull/plugins/webresources steps all hit their
    // "already there -- skipping" branches instead of shelling out to a real pac/dotnet process --
    // the same trick CloneCommandTests.ScaffoldSolutionAsync uses to keep these tests process-free.
    static void SeedScaffoldedRoot(string root, string uniqueName)
    {
        var dataverseSolutionFolder = Path.Combine(root, "Solution");
        Directory.CreateDirectory(Path.Combine(dataverseSolutionFolder, "src"));
        File.WriteAllText(Path.Combine(dataverseSolutionFolder, $"{uniqueName}.cdsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var pluginsFolder = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(pluginsFolder);
        File.WriteAllText(Path.Combine(pluginsFolder, $"{uniqueName}.Plugins.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var webresourcesFolder = Path.Combine(root, "WebResources");
        Directory.CreateDirectory(webresourcesFolder);
        File.WriteAllText(Path.Combine(webresourcesFolder, $"{uniqueName}.WebResources.csproj"), "<Project Sdk=\"Microsoft.Build.NoTargets/1.0\"></Project>");
    }

    // ── R14/R19: pre-write validation runs before anything else ────────────────

    [Fact]
    public async Task CreateSolution_InvalidUniqueName_ThrowsBeforeConnecting()
    {
        var (command, _, orgService) = MakeInitCommand();
        var root = CreateTempRoot();
        try
        {
            var settings = new InitCommand.Settings { PublisherPrefix = "acme" };

            var act = () => command.CreateSolutionAsync(MakeDevEnv(), "class", settings, root, new ProjectConfig(), CancellationToken.None);

            (await act.Should().ThrowAsync<FlowlineException>()).Which.Message.Should().Contain("keyword");
            await orgService.DidNotReceiveWithAnyArgs().RetrieveMultipleAsync(default!, default);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── R5/AE8: no --publisher-prefix + no TTY — errors naming the flag, never connects ──

    [Fact]
    public async Task CreateSolution_NoPublisherPrefix_NonInteractive_ThrowsNamingFlag_WithoutConnecting()
    {
        var (command, _, orgService) = MakeInitCommand(interactive: false);
        var root = CreateTempRoot();
        try
        {
            var act = () => command.CreateSolutionAsync(MakeDevEnv(), "MySolution", new InitCommand.Settings(), root, new ProjectConfig(), CancellationToken.None);

            (await act.Should().ThrowAsync<FlowlineException>()).Which.Message.Should().Contain("--publisher-prefix");
            await orgService.DidNotReceiveWithAnyArgs().RetrieveMultipleAsync(default!, default);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── R5/AE4: no --publisher-prefix + interactive — picker lists existing publishers + create-new ──

    [Fact]
    public async Task PickPublisherPrefixAsync_ExistingPublishers_ListsThemPlusCreateNewChoice()
    {
        var (command, console, orgService) = MakeInitCommand();
        var existing = new Entity("publisher") { ["customizationprefix"] = "acme", ["friendlyname"] = "Acme Corp" };
        orgService.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([existing])));

        console.Input.PushKey(ConsoleKey.Enter); // selects the first listed choice ("acme — Acme Corp")

        var selected = await command.PickPublisherPrefixAsync(orgService, CancellationToken.None);

        selected.Should().Be("acme");
        console.Output.Should().Contain("acme").And.Contain("Create new publisher");
    }

    [Fact]
    public async Task PickPublisherPrefixAsync_CreateNewChoice_PromptsForPrefix()
    {
        var (command, console, orgService) = MakeInitCommand();
        // No existing publishers configured -- MakeInitCommand's default returns an empty
        // EntityCollection, so the only choice is "+ Create new publisher".
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("newco");

        var selected = await command.PickPublisherPrefixAsync(orgService, CancellationToken.None);

        selected.Should().Be("newco");
    }

    // ── R10/R16: DEV role written only after a full success; a post-create failure reports the ──
    // ── created IDs and skips the write ──

    [Fact]
    public async Task CreateSolution_FullSuccess_WritesDevRoleAndEmitsConfirmation()
    {
        var (command, console, orgService) = MakeInitCommand();
        var publisherId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        StubCreate(orgService, publisherId, solutionId);

        var root = CreateTempRoot();
        try
        {
            SeedScaffoldedRoot(root, "MySolution");
            var config = new ProjectConfig();
            var settings = new InitCommand.Settings { PublisherPrefix = "acme" };

            var exitCode = await command.CreateSolutionAsync(MakeDevEnv(), "MySolution", settings, root, config, CancellationToken.None);

            exitCode.Should().Be(0);
            config.DevUrl.Should().Be(DevUrl);
            config.Solution!.UniqueName.Should().Be("MySolution");
            console.Output.Should().Contain("DEV set to");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CreateSolution_BuildFailsAfterCreate_ReportsCreatedIdsForCleanup_DoesNotWriteDevRole()
    {
        var (command, console, orgService) = MakeInitCommand();
        var publisherId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        StubCreate(orgService, publisherId, solutionId);
        command.ValidatePackAndBuildOverride = FailingBuild(13);

        var root = CreateTempRoot();
        try
        {
            SeedScaffoldedRoot(root, "MySolution");
            var config = new ProjectConfig();
            var settings = new InitCommand.Settings { PublisherPrefix = "acme" };

            var exitCode = await command.CreateSolutionAsync(MakeDevEnv(), "MySolution", settings, root, config, CancellationToken.None);

            exitCode.Should().Be(13);
            config.DevUrl.Should().BeNull();
            config.Solution.Should().BeNull();
            console.Output.Should().Contain(publisherId.ToString()).And.Contain(solutionId.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── --force config actually reaches the config-overwrite gate ────────────
    // Regression: the create sequence used to drop `settings` on the floor, so running init over a
    // .flowline naming a different DEV URL failed telling you to pass --force config — and passing it
    // changed nothing, because HasForce is read off the settings that never arrived.

    [Fact]
    public async Task CreateSolution_ExistingDifferentDevUrl_WithForceConfig_OverwritesIt()
    {
        var (command, _, orgService) = MakeInitCommand();
        StubCreate(orgService, Guid.NewGuid(), Guid.NewGuid());

        var root = CreateTempRoot();
        try
        {
            SeedScaffoldedRoot(root, "MySolution");
            var config = new ProjectConfig { DevUrl = "https://stale-dev.crm4.dynamics.com" };
            var settings = new InitCommand.Settings { PublisherPrefix = "acme", Force = ["config"] };

            var exitCode = await command.CreateSolutionAsync(MakeDevEnv(), "MySolution", settings, root, config, CancellationToken.None);

            exitCode.Should().Be(0);
            config.DevUrl.Should().Be(DevUrl);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CreateSolution_ExistingDifferentDevUrl_WithoutForce_NonInteractive_ThrowsNamingForceConfig()
    {
        var (command, _, orgService) = MakeInitCommand(interactive: false);
        StubCreate(orgService, Guid.NewGuid(), Guid.NewGuid());

        var root = CreateTempRoot();
        try
        {
            SeedScaffoldedRoot(root, "MySolution");
            var config = new ProjectConfig { DevUrl = "https://stale-dev.crm4.dynamics.com" };
            var settings = new InitCommand.Settings { PublisherPrefix = "acme" };

            var act = () => command.CreateSolutionAsync(MakeDevEnv(), "MySolution", settings, root, config, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<FlowlineException>()).Which;
            ex.ExitCode.Should().Be(ExitCode.ForceRequired);
            ex.Message.Should().Contain("--force config");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static void StubCreate(IOrganizationServiceAsync2 orgService, Guid publisherId, Guid solutionId)
    {
        orgService.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "publisher")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("publisher", publisherId)])));
        orgService.CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(solutionId));
    }

    // ── KTD3(a): humanize ────────────────────────────────────────────────────

    [Theory]
    [InlineData("MySolution", "My Solution")]
    [InlineData("DWE_Base", "DWE Base")]
    [InlineData("APIGateway", "API Gateway")]
    [InlineData("contoso", "contoso")]
    public void Humanize_ProducesExpectedSpacing(string uniqueName, string expected)
    {
        InitCommand.Humanize(uniqueName).Should().Be(expected);
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
