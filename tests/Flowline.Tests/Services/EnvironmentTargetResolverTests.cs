using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Services;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Flowline.Tests.Services;

[Collection("ProjectConfigConsole")]
public class EnvironmentTargetResolverTests
{
    const string DevUrl = "https://contoso-dev.crm4.dynamics.com/";
    const string TestUrl = "https://contoso-test.crm4.dynamics.com/";
    const string ProdUrl = "https://contoso.crm4.dynamics.com/";

    static (EnvironmentTargetResolver Resolver, TestConsole Console) MakeResolver(bool interactive = false)
    {
        var console = new TestConsole();
        if (interactive) console.Interactive();
        return (new EnvironmentTargetResolver(console), console);
    }

    static Func<string, CancellationToken, Task<EnvironmentInfo?>> EnvInfo(string? type) =>
        (url, _) => Task.FromResult<EnvironmentInfo?>(new EnvironmentInfo { EnvironmentUrl = url, Type = type });

    static Func<string, CancellationToken, Task<EnvironmentInfo?>> NotCalled() =>
        (_, _) => throw new InvalidOperationException("env-info delegate should not have been called for this path");

    // ProjectConfig's "Saved to .flowline" line goes through the static AnsiConsole.Console, not the
    // resolver's own injected console — swap it for the duration of the awaited call so the restore in
    // `finally` can't run ahead of ResolveAsync's own (synchronous, but still awaited) work.
    static async Task<(EnvironmentTargetResult Result, string Output)> RunWithSwappedConsoleAsync(
        Func<Task<EnvironmentTargetResult>> act)
    {
        var original = AnsiConsole.Console;
        var testConsole = new TestConsole();
        AnsiConsole.Console = testConsole;
        try
        {
            var result = await act();
            return (result, testConsole.Output);
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    // ── R5: role keyword ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_Keyword_EmptyKey_ThrowsConfigInvalid_NamingKeyAndEnvFlag()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();

        var act = () => resolver.ResolveAsync("test", config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ConfigInvalid)
            .Which.Message.Should().Contain("TestUrl").And.Contain("--env <url>");
    }

    [Fact]
    public async Task ResolveAsync_Keyword_ConfiguredKey_ReturnsUrl_NoSave_NoEnvInfoCall()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { DevUrl = DevUrl };

        var result = await resolver.ResolveAsync("dev", config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        result.Url.Should().Be(DevUrl);
        result.Role.Should().Be(EnvironmentRole.Dev);
        result.Saved.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_DefaultsToDev_WhenEnvOptionIsNullOrEmpty()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { DevUrl = DevUrl };

        var result = await resolver.ResolveAsync(null, config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        result.Role.Should().Be(EnvironmentRole.Dev);
    }

    // ── URL already saved under a role — normalised compare, no save, no env-info call ────

    [Fact]
    public async Task ResolveAsync_UrlEqualsDevUrl_TrailingSlashAndCaseDiffer_ResolvesToDev_NoSave_NoEnvInfoCall()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { DevUrl = "https://contoso-dev.crm4.dynamics.com/" };

        var result = await resolver.ResolveAsync("HTTPS://CONTOSO-DEV.crm4.dynamics.com", config, onlyRole: null,
            isInteractive: false, new FlowlineSettings(), NotCalled(), CancellationToken.None);

        result.Role.Should().Be(EnvironmentRole.Dev);
        result.Saved.Should().BeFalse();
        result.Url.Should().Be(config.DevUrl);
    }

    // ── AE2: non-interactive new URL, empty DevUrl, suffix-inferred ────────────

    [Fact]
    public async Task ResolveAsync_AE2_NewDevSuffixedUrl_NonInteractive_EmptyDevUrl_SavesAndPrints()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();

        var (result, output) = await RunWithSwappedConsoleAsync(() =>
            resolver.ResolveAsync(DevUrl, config, onlyRole: null, isInteractive: false,
                new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None));

        result.Saved.Should().BeTrue();
        result.Role.Should().Be(EnvironmentRole.Dev);
        result.Url.Should().Be(DevUrl);
        config.DevUrl.Should().Be(DevUrl);
        output.Should().Contain("Saved to .flowline: DevUrl (inferred from URL name)");
    }

    [Fact]
    public async Task ResolveAsync_AE2_SecondResolveWithNoEnvOption_UsesSavedDevUrlSilently()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();
        await resolver.ResolveAsync(DevUrl, config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        var result = await resolver.ResolveAsync(null, config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        result.Url.Should().Be(DevUrl);
        result.Saved.Should().BeFalse();
    }

    // ── AE3: non-interactive new Production-type URL, DEV-only command ─────────

    [Fact]
    public async Task ResolveAsync_AE3_NewProductionUrl_DevOnly_Refused_NothingSaved()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();
        const string url = "https://contoso.crm4.dynamics.com/";

        var act = () => resolver.ResolveAsync(url, config, onlyRole: EnvironmentRole.Dev, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Production"), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
        config.DevUrl.Should().BeNull();
        config.ProdUrl.Should().BeNull();
    }

    // ── AE4: DevUrl already holds a different URL, non-interactive ─────────────

    [Fact]
    public async Task ResolveAsync_AE4_DevUrlHoldsAnotherUrl_NonInteractive_ThrowsForceRequired_NamingForceConfig()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { DevUrl = "https://contoso-dev-old.crm4.dynamics.com/" };

        var act = () => resolver.ResolveAsync(DevUrl, config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ForceRequired)
            .Which.Message.Should().Contain("--force config");
        config.DevUrl.Should().Be("https://contoso-dev-old.crm4.dynamics.com/");
    }

    // ── AE12: non-interactive Sandbox URL with no suffix — can't infer ─────────

    [Fact]
    public async Task ResolveAsync_AE12_SandboxNoSuffix_NonInteractive_ThrowsValidationFailed_NamingFlowlineKey_NothingSaved()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();
        const string url = "https://contoso.crm4.dynamics.com/";

        var act = () => resolver.ResolveAsync(url, config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Which.Message.Should().Contain("DevUrl").And.Contain("TestUrl").And.Contain("UatUrl");
        config.DevUrl.Should().BeNull();
        config.TestUrl.Should().BeNull();
        config.UatUrl.Should().BeNull();
    }

    // ── Interactive: pre-selected inferred role, Enter saves; decline uses once ─

    [Fact]
    public async Task ResolveAsync_Interactive_NewUrl_InferredRolePreselected_EnterSaves()
    {
        var (resolver, console) = MakeResolver(interactive: true);
        var config = new ProjectConfig();
        console.Input.PushKey(ConsoleKey.Enter); // role picker: inferred role (Dev) listed first

        var result = await resolver.ResolveAsync(DevUrl, config, onlyRole: null, isInteractive: true,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        result.Role.Should().Be(EnvironmentRole.Dev);
        result.Saved.Should().BeTrue();
        config.DevUrl.Should().Be(DevUrl);
    }

    [Fact]
    public async Task ResolveAsync_Interactive_NewUrl_Declining_UsesUrlOnce_NothingSaved()
    {
        var (resolver, console) = MakeResolver(interactive: true);
        var config = new ProjectConfig();
        // Choices for an unrestricted, non-Prod-inferred URL: Dev (inferred, first), Test, Uat,
        // "use once" (last) — Prod isn't offered here (Prod only appears when it's the inferred role).
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await resolver.ResolveAsync(DevUrl, config, onlyRole: null, isInteractive: true,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        result.Role.Should().BeNull();
        result.Saved.Should().BeFalse();
        result.Url.Should().Be(DevUrl);
        config.DevUrl.Should().BeNull();
    }

    // ── Interactive + onlyRole: the picker must not offer a role the gate already refused ──

    [Fact]
    public async Task ResolveAsync_Interactive_DevOnly_NewSandboxUrl_PickerOffersOnlyDevAndUseOnce()
    {
        var (resolver, console) = MakeResolver(interactive: true);
        var config = new ProjectConfig();
        console.Input.PushKey(ConsoleKey.Enter); // Dev is the only role choice, listed first

        var result = await resolver.ResolveAsync(DevUrl, config, onlyRole: EnvironmentRole.Dev, isInteractive: true,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        result.Role.Should().Be(EnvironmentRole.Dev);
        console.Output.Should().Contain("DEV").And.NotContain("TEST").And.NotContain("UAT").And.NotContain("PROD");
    }

    // ── A value that's neither a role keyword nor a URL fails before any lookup ─

    [Fact]
    public async Task ResolveAsync_NotAKeywordNorAUrl_ThrowsValidationFailed_NoEnvInfoCall()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();

        var act = () => resolver.ResolveAsync("staging", config, onlyRole: null, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    // ── Gate: DEV-only commands refuse anything but DEV, before connecting ─────

    [Fact]
    public async Task ResolveAsync_Gate_KeywordProd_DevOnly_ThrowsValidationFailed_NoEnvInfoCall()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { ProdUrl = "https://contoso.crm4.dynamics.com/" };

        var act = () => resolver.ResolveAsync("prod", config, onlyRole: EnvironmentRole.Dev, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Fact]
    public async Task ResolveAsync_Gate_UrlEqualsConfiguredTestUrl_DevOnly_ThrowsValidationFailed_NoEnvInfoCall()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { TestUrl = TestUrl };

        var act = () => resolver.ResolveAsync(TestUrl, config, onlyRole: EnvironmentRole.Dev, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Fact]
    public async Task ResolveAsync_Gate_NewProductionTypedUrl_DevOnly_ThrowsValidationFailed()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();
        const string url = "https://contoso.crm4.dynamics.com/";

        var act = () => resolver.ResolveAsync(url, config, onlyRole: EnvironmentRole.Dev, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Production"), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Theory]
    [InlineData("https://contoso-test.crm4.dynamics.com/", "TEST")]
    [InlineData("https://contoso-uat.crm4.dynamics.com/", "UAT")]
    public async Task ResolveAsync_Gate_NewSandboxUrlWithTestOrUatSuffix_DevOnly_Refused_NothingSaved(string url, string label)
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();

        var act = () => resolver.ResolveAsync(url, config, onlyRole: EnvironmentRole.Dev, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Where(e => e.Message.Contains(label) && e.Message.Contains("only accepts DEV"));
        config.TestUrl.Should().BeNull();
        config.UatUrl.Should().BeNull();
        config.DevUrl.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_Gate_NewSandboxUrlWithTestSuffix_DevOnly_Interactive_RefusedBeforeAnyPicker()
    {
        var (resolver, console) = MakeResolver(interactive: true);
        var config = new ProjectConfig();

        var act = () => resolver.ResolveAsync(TestUrl, config, onlyRole: EnvironmentRole.Dev, isInteractive: true,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
        console.Output.Should().NotContain("Save");
    }

    [Fact]
    public async Task ResolveAsync_Interactive_OverwriteDeclined_UsesTypedUrlOnce_KeepsStoredUrl()
    {
        var (resolver, console) = MakeResolver(interactive: true);
        var config = new ProjectConfig { DevUrl = "https://old-dev.crm4.dynamics.com/" };
        const string newUrl = "https://contoso-dev.crm4.dynamics.com/";

        // Role picker on the resolver's console: Dev is inferred and listed first -> Enter. The overwrite
        // prompt runs on the static AnsiConsole.Console (ProjectConfig's seam), so that one answers 'n'.
        console.Input.PushKey(ConsoleKey.Enter);
        var original = AnsiConsole.Console;
        var promptConsole = new TestConsole();
        promptConsole.Interactive();
        promptConsole.Input.PushTextWithEnter("n");
        AnsiConsole.Console = promptConsole;
        EnvironmentTargetResult result;
        try
        {
            result = await resolver.ResolveAsync(newUrl, config, onlyRole: EnvironmentRole.Dev, isInteractive: true,
                new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);
        }
        finally
        {
            AnsiConsole.Console = original;
        }

        result.Url.Should().Be(newUrl);
        result.Saved.Should().BeFalse();
        config.DevUrl.Should().Be("https://old-dev.crm4.dynamics.com/");
        console.Output.Should().Contain("once");
    }

    [Fact]
    public async Task ResolveAsync_Gate_SandboxUrlWithDevSuffix_DevOnly_Passes()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();

        var result = await resolver.ResolveAsync(DevUrl, config, onlyRole: EnvironmentRole.Dev, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        result.Role.Should().Be(EnvironmentRole.Dev);
        result.Saved.Should().BeTrue();
    }

    // ── Gate: onlyRole Prod (provision) refuses anything but PROD, before connecting ──────

    [Fact]
    public async Task ResolveAsync_Gate_KeywordDev_ProdOnly_ThrowsValidationFailed_NamingProd()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { DevUrl = DevUrl };

        var act = () => resolver.ResolveAsync("dev", config, onlyRole: EnvironmentRole.Prod, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Which.Message.Should().Contain("PROD");
    }

    [Fact]
    public async Task ResolveAsync_Gate_UrlEqualsConfiguredDevUrl_ProdOnly_ThrowsValidationFailed_NoEnvInfoCall()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { DevUrl = DevUrl };

        var act = () => resolver.ResolveAsync(DevUrl, config, onlyRole: EnvironmentRole.Prod, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Fact]
    public async Task ResolveAsync_Gate_NewSandboxUrl_ProdOnly_Refused_NamingSandboxType_NothingSaved()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();

        var act = () => resolver.ResolveAsync(ProdUrl, config, onlyRole: EnvironmentRole.Prod, isInteractive: false,
            new FlowlineSettings(), EnvInfo("Sandbox"), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Which.Message.Should().Contain("Sandbox").And.Contain("only accepts PROD");
        config.ProdUrl.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_NewProductionTypedUrl_ProdOnly_NonInteractive_SavesAsProdUrl()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig();

        var (result, output) = await RunWithSwappedConsoleAsync(() =>
            resolver.ResolveAsync(ProdUrl, config, onlyRole: EnvironmentRole.Prod, isInteractive: false,
                new FlowlineSettings(), EnvInfo("Production"), CancellationToken.None));

        result.Saved.Should().BeTrue();
        result.Role.Should().Be(EnvironmentRole.Prod);
        result.Url.Should().Be(ProdUrl);
        config.ProdUrl.Should().Be(ProdUrl);
        output.Should().Contain("Saved to .flowline: ProdUrl");
    }

    [Fact]
    public async Task ResolveAsync_ProdOnly_BlankEnv_ResolvesConfiguredProdUrl()
    {
        var (resolver, _) = MakeResolver();
        var config = new ProjectConfig { ProdUrl = ProdUrl };

        var result = await resolver.ResolveAsync(null, config, onlyRole: EnvironmentRole.Prod, isInteractive: false,
            new FlowlineSettings(), NotCalled(), CancellationToken.None);

        result.Role.Should().Be(EnvironmentRole.Prod);
        result.Url.Should().Be(ProdUrl);
        result.Saved.Should().BeFalse();
    }

    // ── IsRoleKeyword / EnsureUsableStandaloneEnv (U3): standalone push/generate have no ProjectConfig,
    // so a bare role keyword has nothing to resolve against and must be refused rather than silently
    // treated as a literal URL. ──

    [Theory]
    [InlineData("dev")]
    [InlineData("DEV")]
    [InlineData("test")]
    [InlineData("uat")]
    [InlineData("prod")]
    public void IsRoleKeyword_RecognizedRoleNames_ReturnsTrue(string value) =>
        EnvironmentTargetResolver.IsRoleKeyword(value).Should().BeTrue();

    [Theory]
    [InlineData("https://contoso-dev.crm4.dynamics.com/")]
    [InlineData("staging")]
    [InlineData("")]
    public void IsRoleKeyword_UrlOrUnrecognizedValue_ReturnsFalse(string value) =>
        EnvironmentTargetResolver.IsRoleKeyword(value).Should().BeFalse();

    [Fact]
    public void EnsureUsableStandaloneEnv_RoleKeyword_ThrowsNamingEnvUrl()
    {
        var act = () => EnvironmentTargetResolver.EnsureUsableStandaloneEnv("dev");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Where(e => e.Message.Contains("--env <url>"));
    }

    [Fact]
    public void EnsureUsableStandaloneEnv_Url_DoesNotThrow()
    {
        var act = () => EnvironmentTargetResolver.EnsureUsableStandaloneEnv(DevUrl);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureUsableStandaloneEnv_NullOrEmpty_DoesNotThrow()
    {
        var act = () => EnvironmentTargetResolver.EnsureUsableStandaloneEnv(null);

        act.Should().NotThrow();
    }
}
