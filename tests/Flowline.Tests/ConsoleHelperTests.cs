using Flowline.Core;
using Flowline.Utils;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests;

// ConsoleHelper.Confirm reads the static ambient AnsiConsole.Console (no injected IAnsiConsole to
// substitute), so the interactive test below has to swap that global for its duration. Serialized
// against every other test in this collection so no concurrently-running test observes the swap.
[CollectionDefinition(nameof(ConsoleStaticSwapCollection), DisableParallelization = true)]
public class ConsoleStaticSwapCollection;

[Collection(nameof(ConsoleStaticSwapCollection))]
public class ConsoleHelperTests
{
    // Non-interactivity is now a console capability, not an env-var probe — a default TestConsole
    // (Interactive = false) is what makes these the "no TTY" cases.
    static IDisposable NonInteractiveConsole()
    {
        var previous = AnsiConsole.Console;
        AnsiConsole.Console = new TestConsole();
        return new Restore(() => AnsiConsole.Console = previous);
    }

    sealed class Restore(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsConfig_ReturnsTrueWithoutPrompting()
    {
        using var _ = NonInteractiveConsole();

        var settings = new FlowlineSettings { Force = ["config"] };
        ConsoleHelper.Confirm("Overwrite it?", false, settings, "config").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsAll_ReturnsTrueWithoutPrompting()
    {
        using var _ = NonInteractiveConsole();

        var settings = new FlowlineSettings { Force = ["all"] };
        ConsoleHelper.Confirm("Overwrite it?", false, settings, "config").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceEmpty_ThrowsForceRequiredNamingConfig()
    {
        using var _ = NonInteractiveConsole();

        var settings = new FlowlineSettings { Force = [] };
        var act = () => ConsoleHelper.Confirm("Overwrite it?", false, settings, "config");
        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force config"));
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsMatchingSpecifier_ReturnsTrueWithoutPrompting()
    {
        using var _ = NonInteractiveConsole();

        var settings = new FlowlineSettings { Force = ["first-import"] };
        ConsoleHelper.Confirm("Continue?", false, settings, "first-import").Should().BeTrue();
    }

    [Fact]
    public void Confirm_Interactive_Force_ReturnsTrueWithoutPromptingEvenWhenInteractive()
    {
        var previousConsole = AnsiConsole.Console;
        var testConsole = new TestConsole();
        testConsole.Interactive();
        // No input pushed — if Confirm tried to prompt, TestConsole would throw on the empty queue.
        AnsiConsole.Console = testConsole;
        try
        {
            var settings = new FlowlineSettings { Force = ["first-import"] };

            ConsoleHelper.Confirm("Continue?", false, settings, "first-import").Should().BeTrue();
        }
        finally { AnsiConsole.Console = previousConsole; }
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsDifferentSpecifier_ThrowsNamingRequestedSpecifier()
    {
        using var _ = NonInteractiveConsole();

        var settings = new FlowlineSettings { Force = ["config"] };
        var act = () => ConsoleHelper.Confirm("Continue?", false, settings, "first-import");
        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force first-import"));
    }

    // The three IsInteractive_ShouldReturnFalse_When*EnvVarIsSet tests were removed with
    // ConsoleHelper.IsInteractive itself — interactivity is Spectre's Capabilities.Interactive now,
    // which its own CI profile enrichers already drive. DetectCIPlatform below still reads env vars,
    // but only to name the platform, never to gate a prompt.

    static readonly string[] s_ciVars = ["GITHUB_ACTIONS", "TF_BUILD", "JENKINS_URL", "CI"];

    static Dictionary<string, string?> SaveAndClearCiVars()
    {
        var saved = s_ciVars.ToDictionary(v => v, v => Environment.GetEnvironmentVariable(v));
        foreach (var v in s_ciVars) Environment.SetEnvironmentVariable(v, null);
        return saved;
    }

    static void RestoreCiVars(Dictionary<string, string?> saved)
    {
        foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
    }

    [Fact]
    public void DetectCIPlatform_ShouldReturnNull_WhenNoCiVarsSet()
    {
        var saved = SaveAndClearCiVars();
        try { ConsoleHelper.DetectCIPlatform().Should().BeNull(); }
        finally { RestoreCiVars(saved); }
    }

    [Theory]
    [InlineData("GITHUB_ACTIONS", "true", "github")]
    [InlineData("TF_BUILD", "True", "azuredevops")]
    [InlineData("JENKINS_URL", "http://jenkins.example.com", "jenkins")]
    [InlineData("CI", "true", "unknown")]
    public void DetectCIPlatform_ShouldReturnExpectedPlatform_ForKnownCiVar(string envVar, string envValue, string expected)
    {
        var saved = SaveAndClearCiVars();
        Environment.SetEnvironmentVariable(envVar, envValue);
        try { ConsoleHelper.DetectCIPlatform().Should().Be(expected); }
        finally { RestoreCiVars(saved); }
    }
}
