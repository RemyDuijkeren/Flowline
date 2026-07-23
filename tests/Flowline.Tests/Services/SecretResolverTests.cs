using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Services;
using FluentAssertions;
using Spectre.Console.Testing;

namespace Flowline.Tests.Services;

/// <summary>Test double that overrides IsInteractive() to avoid reading static AnsiConsole.Profile.</summary>
file sealed class FakeSecretResolver(TestConsole console, bool interactive) : SecretResolver(console)
{
    protected override bool IsInteractive() => interactive;
}

public class SecretResolverTests
{
    static PacProfile SpProfile(string applicationId = "app-id", string? name = "MyProfile") =>
        new() { Kind = "ServicePrincipal", ApplicationId = applicationId, Name = name };

    // ── 1. --secret flag wins ────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_FlagSet_ReturnsFlagValue()
    {
        var resolver = new SecretResolver(new TestConsole());

        var result = await resolver.ResolveAsync(SpProfile(), secretFlag: "flag-secret");

        result.Should().Be("flag-secret");
    }

    // ── 2. AZURE_CLIENT_SECRET env var ──────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NoFlag_EnvVarSet_ReturnsEnvValue()
    {
        var original = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "env-secret");
            var resolver = new SecretResolver(new TestConsole());

            var result = await resolver.ResolveAsync(SpProfile(), secretFlag: null);

            result.Should().Be("env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", original);
        }
    }

    // ── 3. Flag wins over env var ────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_FlagAndEnvVarSet_FlagWins()
    {
        var original = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "env-secret");
            var resolver = new SecretResolver(new TestConsole());

            var result = await resolver.ResolveAsync(SpProfile(), secretFlag: "flag-secret");

            result.Should().Be("flag-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", original);
        }
    }

    // ── 4. Non-interactive, no flag, no env var → throws ────────────────────

    [Fact]
    public async Task ResolveAsync_NoFlag_NoEnvVar_NonInteractive_ThrowsFlowlineException()
    {
        var original = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);
            var resolver = new FakeSecretResolver(new TestConsole(), interactive: false);

            var act = async () => await resolver.ResolveAsync(SpProfile(), secretFlag: null);

            await act.Should().ThrowAsync<FlowlineException>()
                .Where(ex => ex.Message.Contains("AZURE_CLIENT_SECRET") && ex.Message.Contains("--client-secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", original);
        }
    }

    [Fact]
    public async Task ResolveAsync_NonInteractive_ExitCode_IsNotAuthenticated()
    {
        var original = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);
            var resolver = new FakeSecretResolver(new TestConsole(), interactive: false);

            var act = async () => await resolver.ResolveAsync(SpProfile(), secretFlag: null);

            await act.Should().ThrowAsync<FlowlineException>()
                .Where(ex => ex.ExitCode == ExitCode.NotAuthenticated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", original);
        }
    }

    [Fact]
    public async Task ResolveAsync_EmptyStringName_NonInteractive_FallsBackToApplicationId()
    {
        // Live bug (2026-07-23): PAC's authprofiles_v2.json gives an unnamed profile Name = "", not
        // null — a bare `Name ?? ApplicationId` chain never falls through for that shape.
        var original = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);
            var resolver = new FakeSecretResolver(new TestConsole(), interactive: false);

            var act = async () => await resolver.ResolveAsync(SpProfile(applicationId: "app-id", name: ""), secretFlag: null);

            await act.Should().ThrowAsync<FlowlineException>()
                .Where(ex => ex.Message.Contains("app-id") && !ex.Message.Contains("''"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", original);
        }
    }

    // ── 5. Interactive → prompt shown ───────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NoFlag_NoEnvVar_Interactive_PromptsAndReturnsInput()
    {
        // FakeSecretResolver bypasses the static AnsiConsole check so the test
        // doesn't rely on global state.
        var original = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);

            var testConsole = new TestConsole();
            testConsole.Input.PushTextWithEnter("prompt-secret");
            var resolver = new FakeSecretResolver(testConsole, interactive: true);

            var result = await resolver.ResolveAsync(SpProfile(), secretFlag: null);

            result.Should().Be("prompt-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", original);
        }
    }

    // ── 6. Secret never appears in console output ────────────────────────────

    [Fact]
    public async Task ResolveAsync_FlagSet_SecretNotInConsoleOutput()
    {
        var testConsole = new TestConsole();
        var resolver = new SecretResolver(testConsole);

        await resolver.ResolveAsync(SpProfile(), secretFlag: "super-secret-value");

        testConsole.Output.Should().NotContain("super-secret-value");
    }

    [Fact]
    public async Task ResolveAsync_EnvVarSet_SecretNotInConsoleOutput()
    {
        var original = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "env-secret-value");
            var testConsole = new TestConsole();
            var resolver = new SecretResolver(testConsole);

            await resolver.ResolveAsync(SpProfile(), secretFlag: null);

            testConsole.Output.Should().NotContain("env-secret-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", original);
        }
    }
}
