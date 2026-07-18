using Flowline;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Services;
using FluentAssertions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests.Services;

public class ProfileResolutionServiceTests
{
    static readonly string EnvironmentUrl = "https://automatevalue-dev.crm4.dynamics.com";

    static ProfileResolutionService MakeService(
        out TestConsole console,
        ProfileResolutionResult resolvedResult,
        bool isProfileActive = true,
        bool isInteractive = false,
        bool autoSwitchProfile = false,
        IReadOnlyList<PacProfile>? allProfiles = null,
        Func<PacProfile, bool>? isProfileActiveOverride = null)
    {
        console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var svc = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions { AutoSwitchProfile = autoSwitchProfile })
        {
            FindBestProfileOverride = _ => resolvedResult,
            IsProfileActiveOverride = isProfileActiveOverride ?? (_ => isProfileActive),
            IsInteractiveOverride = () => isInteractive,
            GetPacProfilesOverride = () => allProfiles ?? []
        };
        return svc;
    }

    static PacProfile MakeProfile(string? name = "MyProfile", string? kind = "DATAVERSE",
        string? resource = "https://automatevalue-dev.crm4.dynamics.com")
        => new() { Name = name, Kind = kind, Resource = resource };

    // ── ProfileFound ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ProfileFound_ReturnsProfile()
    {
        var profile = MakeProfile();
        var svc = MakeService(out _, new ProfileFound(profile));

        var result = await svc.ResolveAsync(EnvironmentUrl);

        result.Should().BeSameAs(profile);
    }

    [Fact]
    public async Task ProfileFound_EmitsStatusLine()
    {
        var profile = MakeProfile(name: "MyProfile", kind: "DATAVERSE");
        var svc = MakeService(out var console, new ProfileFound(profile));

        await svc.ResolveAsync(EnvironmentUrl);

        console.Output.Should().Contain("Resolved PAC auth profile 'MyProfile' (DATAVERSE)");
    }

    [Fact]
    public async Task ProfileFound_NameEmpty_EmitsUnnamedStatusLine()
    {
        var profile = MakeProfile(name: "", kind: "UNIVERSAL", resource: EnvironmentUrl);
        var svc = MakeService(out var console, new ProfileFound(profile));

        await svc.ResolveAsync(EnvironmentUrl);

        // TestConsole may word-wrap long lines; assert the key parts appear in output
        console.Output.Should().Contain("Resolved PAC auth profile (unnamed, UNIVERSAL)");
        console.Output.Should().Contain(EnvironmentUrl);
    }

    [Fact]
    public async Task ProfileFound_NameNull_EmitsUnnamedStatusLine()
    {
        var profile = MakeProfile(name: null, kind: "DATAVERSE", resource: EnvironmentUrl);
        var svc = MakeService(out var console, new ProfileFound(profile));

        await svc.ResolveAsync(EnvironmentUrl);

        // TestConsole may word-wrap long lines; assert the key parts appear in output
        console.Output.Should().Contain("Resolved PAC auth profile (unnamed, DATAVERSE)");
        console.Output.Should().Contain(EnvironmentUrl);
    }

    // ── ProfileAmbiguous — non-interactive ───────────────────────────────────

    [Fact]
    public async Task ProfileAmbiguous_NonInteractive_ThrowsFlowlineException()
    {
        var candidates = new List<PacProfile>
        {
            MakeProfile(name: "Alpha", kind: "DATAVERSE"),
            MakeProfile(name: "Beta", kind: "UNIVERSAL")
        };
        // CI env var makes ConsoleHelper.IsInteractive return false
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var svc = MakeService(out _, new ProfileAmbiguous(candidates));
            var act = () => svc.ResolveAsync(EnvironmentUrl);

            await act.Should().ThrowAsync<FlowlineException>()
                .Where(ex => ex.ExitCode == ExitCode.NotAuthenticated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", null);
        }
    }

    [Fact]
    public async Task ProfileAmbiguous_NonInteractive_ExceptionMessageContainsCandidates()
    {
        var candidates = new List<PacProfile>
        {
            MakeProfile(name: "Alpha", kind: "DATAVERSE"),
            MakeProfile(name: "Beta", kind: "UNIVERSAL")
        };
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var svc = MakeService(out _, new ProfileAmbiguous(candidates));

            var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

            ex.Message.Should().Contain("Alpha");
            ex.Message.Should().Contain("Beta");
            ex.Message.Should().Contain("pac auth select");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", null);
        }
    }

    // ── ProfileNotFound ──────────────────────────────────────────────────────

    [Fact]
    public async Task ProfileNotFound_ThrowsFlowlineException()
    {
        var svc = MakeService(out _, new ProfileNotFound(EnvironmentUrl));

        var act = () => svc.ResolveAsync(EnvironmentUrl);

        await act.Should().ThrowAsync<FlowlineException>()
            .Where(ex => ex.ExitCode == ExitCode.NotAuthenticated);
    }

    [Fact]
    public async Task ProfileNotFound_ExceptionMessageContainsPacAuthCreate()
    {
        var svc = MakeService(out _, new ProfileNotFound(EnvironmentUrl));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.Message.Should().Contain("pac auth create");
        ex.Message.Should().Contain(EnvironmentUrl.TrimEnd('/'));
    }

    [Fact]
    public async Task ProfileNotFound_ExceptionMessageContainsNameSuggestion()
    {
        // automatevalue-dev.crm4.dynamics.com → first segment = "automatevalue-dev"
        // split on "-" → ["automatevalue", "dev"] → TitleCase → ["Automatevalue", "Dev"] → "Automatevalue-Dev"
        var svc = MakeService(out _, new ProfileNotFound(EnvironmentUrl));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.Message.Should().Contain("Automatevalue-Dev");
    }

    // ── Guard — active profile enforcement (R2-R8) ──────────────────────────

    [Fact]
    public async Task Guard_ProfileAlreadyActive_NoPromptNoSwitch()
    {
        var profile = MakeProfile();
        var switchCalls = 0;
        var svc = MakeService(out var console, new ProfileFound(profile), isProfileActive: true);
        svc.SelectAuthProfileOverride = (_, _, _) => { switchCalls++; return Task.CompletedTask; };

        var result = await svc.ResolveAsync(EnvironmentUrl);

        result.Should().BeSameAs(profile);
        switchCalls.Should().Be(0);
        console.Output.Should().NotContain("Switched active PAC auth profile");
    }

    [Fact]
    public async Task Guard_NonInteractiveMismatch_ThrowsWithCorrectiveCommand()
    {
        var profile = MakeProfile(name: "MyProfile");
        var svc = MakeService(out _, new ProfileFound(profile), isProfileActive: false, isInteractive: false);

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.ExitCode.Should().Be(ExitCode.NotAuthenticated);
        ex.Message.Should().Contain("pac auth select --name 'MyProfile'");
    }

    [Fact]
    public async Task Guard_NonInteractiveMismatch_UnnamedProfile_CorrectiveCommandUsesIndex()
    {
        var profile = MakeProfile(name: null, kind: "DATAVERSE");
        var allProfiles = new List<PacProfile> { MakeProfile(name: "Other"), profile };
        var svc = MakeService(out _, new ProfileFound(profile), isProfileActive: false, isInteractive: false, allProfiles: allProfiles);

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.Message.Should().Contain("pac auth select --index '2'");
    }

    [Fact]
    public async Task Guard_NonInteractiveMismatch_ForceFlagDoesNotBypass()
    {
        // The guard never consults --force at all — proven here by asserting it still throws
        // regardless of RuntimeOptions.Force being populated for an unrelated hazard.
        var profile = MakeProfile();
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var svc = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions { Force = ["all"] })
        {
            FindBestProfileOverride = _ => new ProfileFound(profile),
            IsProfileActiveOverride = _ => false,
            IsInteractiveOverride = () => false,
            GetPacProfilesOverride = () => [profile]
        };

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.ExitCode.Should().Be(ExitCode.NotAuthenticated);
    }

    [Fact]
    public async Task Guard_InteractiveMismatchConfirmed_SwitchesAndProceeds()
    {
        var profile = MakeProfile();
        var switchCalls = 0;
        var switched = false;
        var svc = MakeService(out var console, new ProfileFound(profile), isInteractive: true, allProfiles: [profile],
            isProfileActiveOverride: _ => switched);
        svc.SelectAuthProfileOverride = (_, _, _) => { switchCalls++; switched = true; return Task.CompletedTask; };
        console.Interactive();
        console.Input.PushTextWithEnter("y");

        var result = await svc.ResolveAsync(EnvironmentUrl);

        result.Should().BeSameAs(profile);
        switchCalls.Should().Be(1);
        console.Output.Should().Contain("Switched active PAC auth profile");
        console.Output.Should().Contain("Environment"); // R3: profile table rendered before the confirm prompt
    }

    [Fact]
    public async Task Guard_InteractiveMismatchDeclined_ThrowsSameExceptionAsNonInteractive()
    {
        var profile = MakeProfile(name: "MyProfile");
        var switchCalls = 0;
        var svc = MakeService(out var console, new ProfileFound(profile), isProfileActive: false, isInteractive: true, allProfiles: [profile]);
        svc.SelectAuthProfileOverride = (_, _, _) => { switchCalls++; return Task.CompletedTask; };
        console.Interactive();
        console.Input.PushTextWithEnter("n");

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.ExitCode.Should().Be(ExitCode.NotAuthenticated);
        ex.Message.Should().Contain("pac auth select --name 'MyProfile'");
        switchCalls.Should().Be(0);
    }

    [Fact]
    public async Task Guard_InteractiveMismatchBareEnter_DefaultsToDecline()
    {
        var profile = MakeProfile(name: "MyProfile");
        var switchCalls = 0;
        var svc = MakeService(out var console, new ProfileFound(profile), isProfileActive: false, isInteractive: true, allProfiles: [profile]);
        svc.SelectAuthProfileOverride = (_, _, _) => { switchCalls++; return Task.CompletedTask; };
        console.Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.ExitCode.Should().Be(ExitCode.NotAuthenticated);
        switchCalls.Should().Be(0);
    }

    [Fact]
    public async Task Guard_AutoSwitchNonInteractive_SwitchesWithoutPromptOrException()
    {
        var profile = MakeProfile();
        var switchCalls = 0;
        var switched = false;
        var svc = MakeService(out var console, new ProfileFound(profile), isInteractive: false, autoSwitchProfile: true, allProfiles: [profile],
            isProfileActiveOverride: _ => switched);
        svc.SelectAuthProfileOverride = (_, _, _) => { switchCalls++; switched = true; return Task.CompletedTask; };

        var result = await svc.ResolveAsync(EnvironmentUrl);

        result.Should().BeSameAs(profile);
        switchCalls.Should().Be(1);
        console.Output.Should().Contain("Switched active PAC auth profile");
    }

    [Fact]
    public async Task Guard_AutoSwitchInteractive_SwitchesWithoutShowingPrompt()
    {
        var profile = MakeProfile();
        var switchCalls = 0;
        var switched = false;
        var svc = MakeService(out var console, new ProfileFound(profile), isInteractive: true, autoSwitchProfile: true, allProfiles: [profile],
            isProfileActiveOverride: _ => switched);
        svc.SelectAuthProfileOverride = (_, _, _) => { switchCalls++; switched = true; return Task.CompletedTask; };
        // No input pushed — if a prompt were shown, TestConsole would throw on empty input queue.

        var result = await svc.ResolveAsync(EnvironmentUrl);

        result.Should().BeSameAs(profile);
        switchCalls.Should().Be(1);
        console.Output.Should().NotContain("Switch active PAC auth profile to");
    }

    [Fact]
    public async Task Guard_TwoDifferentUrls_ReEvaluatesIndependently()
    {
        var profileA = MakeProfile(name: "A", resource: "https://a.crm4.dynamics.com");
        var profileB = MakeProfile(name: "B", resource: "https://b.crm4.dynamics.com");
        var activeChecks = new List<string>();
        var switchCalls = 0;
        var switched = new HashSet<string>();

        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var svc = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions { AutoSwitchProfile = true })
        {
            IsInteractiveOverride = () => false,
            GetPacProfilesOverride = () => [profileA, profileB],
            IsProfileActiveOverride = p => { activeChecks.Add(p.Name!); return switched.Contains(p.Name!); },
            SelectAuthProfileOverride = (p, _, _) => { switchCalls++; switched.Add(p.Name!); return Task.CompletedTask; }
        };

        svc.FindBestProfileOverride = _ => new ProfileFound(profileA);
        await svc.ResolveAsync(profileA.Resource!);

        svc.FindBestProfileOverride = _ => new ProfileFound(profileB);
        await svc.ResolveAsync(profileB.Resource!);

        // Each URL is checked twice: once to detect the mismatch, once after switching to confirm it took.
        activeChecks.Should().Equal("A", "A", "B", "B");
        switchCalls.Should().Be(2);
    }

    [Fact]
    public async Task Guard_SelectWrapperFails_ThrowsAndDoesNotPrintAnnouncement()
    {
        var profile = MakeProfile();
        var svc = MakeService(out var console, new ProfileFound(profile), isProfileActive: false, autoSwitchProfile: true, allProfiles: [profile]);
        svc.SelectAuthProfileOverride = (_, _, _) =>
            throw new FlowlineException(ExitCode.NotAuthenticated, "'pac auth select --name MyProfile' failed: boom");

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.ExitCode.Should().Be(ExitCode.NotAuthenticated);
        ex.Message.Should().Contain("boom");
        console.Output.Should().NotContain("Switched active PAC auth profile");
    }

    [Fact]
    public async Task Guard_SelectReportsSuccessButProfileStillNotActive_ThrowsAndDoesNotPrintAnnouncement()
    {
        // pac auth select can exit 0 without authprofiles_v2.json actually reflecting the change
        // (e.g. a race with a concurrent 'pac auth select') — the guard must not trust the exit code alone.
        var profile = MakeProfile();
        var svc = MakeService(out var console, new ProfileFound(profile), isProfileActive: false, autoSwitchProfile: true, allProfiles: [profile]);
        svc.SelectAuthProfileOverride = (_, _, _) => Task.CompletedTask;

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.ExitCode.Should().Be(ExitCode.NotAuthenticated);
        ex.Message.Should().Contain("still isn't active");
        console.Output.Should().NotContain("Switched active PAC auth profile");
    }

    // ── AutoSwitchProfile flag parsing (-a / --auto-select-auth-profile) ────

    [Fact]
    public void AutoSwitchProfile_ShortAndLongForm_ParseToSameProperty()
    {
        // No CommandApp-level short-form-alias parsing test exists elsewhere in this codebase to mirror
        // (e.g. for -v/-f) — asserting the attribute directly is the simplest equivalent check that -a
        // and --auto-select-auth-profile both bind to FlowlineSettings.AutoSwitchProfile.
        var property = typeof(FlowlineSettings).GetProperty(nameof(FlowlineSettings.AutoSwitchProfile))!;
        var option = (CommandOptionAttribute)property.GetCustomAttributes(typeof(CommandOptionAttribute), false).Single();

        option.LongNames.Should().Contain("auto-select-auth-profile");
        option.ShortNames.Should().Contain("a");
    }

    // ── BuildNameSuggestion ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://automatevalue-dev.crm4.dynamics.com", "Automatevalue-Dev")]
    [InlineData("https://myorg.crm.dynamics.com", "Myorg")]
    [InlineData("https://contoso.crm4.dynamics.com", "Contoso")]
    [InlineData("https://my-org-test.crm4.dynamics.com", "My-Org-Test")]
    public void BuildNameSuggestion_VariousUrls_ReturnsExpected(string url, string expected)
    {
        ProfileResolutionService.BuildNameSuggestion(url).Should().Be(expected);
    }

    [Fact]
    public void BuildNameSuggestion_UrlWithPort_UsesHostOnly()
    {
        // Only host segment first part used — port should not affect the result
        ProfileResolutionService.BuildNameSuggestion("https://myorg.crm4.dynamics.com:443/api/data")
            .Should().Be("Myorg");
    }
}
