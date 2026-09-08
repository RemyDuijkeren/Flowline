using System.ComponentModel;
using System.Reflection;
using Flowline.Commands;
using Spectre.Console.Cli;
using Xunit;

namespace Flowline.Tests;

// Regression coverage for the --managed FlagValue<bool> binding on clone/sync.
// [CommandOption("--managed")] (no value placeholder, no [DefaultValue]) left
// settings.IncludeManaged null when the flag was passed bare, causing an NRE at
// `settings.IncludeManaged.IsSet` — see CloneCommand.FindUnmanagedSourceAsync.
public class ManagedFlagBindingTests
{
    sealed class CloneProbeCommand : Command<CloneCommand.Settings>
    {
        public static CloneCommand.Settings? Captured;

        protected override int Execute(CommandContext context, CloneCommand.Settings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    sealed class ConfigureProbeCommand : Command<ConfigureCommand.Settings>
    {
        public static ConfigureCommand.Settings? Captured;

        protected override int Execute(CommandContext context, ConfigureCommand.Settings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    sealed class SyncProbeCommand : Command<SyncCommand.Settings>
    {
        public static SyncCommand.Settings? Captured;

        protected override int Execute(CommandContext context, SyncCommand.Settings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    [Fact]
    public void Clone_ManagedFlagBare_IncludeManagedIsSetTrue()
    {
        var app = new CommandApp<CloneProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["MySolution", "--managed"]);

        Assert.Equal(0, result);
        Assert.NotNull(CloneProbeCommand.Captured!.IncludeManaged);
        Assert.True(CloneProbeCommand.Captured!.IncludeManaged.IsSet);
        Assert.True(CloneProbeCommand.Captured!.IncludeManaged.Value);
    }

    [Fact]
    public void Clone_ManagedFlagExplicitFalse_IncludeManagedIsSetFalse()
    {
        var app = new CommandApp<CloneProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["MySolution", "--managed", "false"]);

        Assert.Equal(0, result);
        Assert.NotNull(CloneProbeCommand.Captured!.IncludeManaged);
        Assert.True(CloneProbeCommand.Captured!.IncludeManaged.IsSet);
        Assert.False(CloneProbeCommand.Captured!.IncludeManaged.Value);
    }

    [Fact]
    public void Clone_ManagedFlagAbsent_IncludeManagedIsNotSet()
    {
        var app = new CommandApp<CloneProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["MySolution"]);

        Assert.Equal(0, result);
        Assert.NotNull(CloneProbeCommand.Captured!.IncludeManaged);
        Assert.False(CloneProbeCommand.Captured!.IncludeManaged.IsSet);
    }

    // Regression: clone's positional was declared REQUIRED ("<solution>"), so Spectre rejected
    // `flowline clone` with no args at parse time — before ExecuteFlowlineAsync ran. That made the
    // interactive pick-or-create path (ShouldPickOrCreate keys off an empty settings.Solution)
    // unreachable via the CLI. The arg must be optional ("[solution]") so a bare invocation binds
    // with a null Solution and reaches the gate. Goes through real Spectre binding, which the direct
    // ShouldPickOrCreate/PickOrCreateAsync unit tests bypass (that's why the dead path shipped green).
    [Fact]
    public void Clone_NoSolutionArg_BindsWithNullSolution()
    {
        var app = new CommandApp<CloneProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run([]);

        Assert.Equal(0, result);
        Assert.Null(CloneProbeCommand.Captured!.Solution);
    }

    [Fact]
    public void Sync_ManagedFlagBare_IncludeManagedIsSetTrue()
    {
        var app = new CommandApp<SyncProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["--managed"]);

        Assert.Equal(0, result);
        Assert.NotNull(SyncProbeCommand.Captured!.IncludeManaged);
        Assert.True(SyncProbeCommand.Captured!.IncludeManaged.IsSet);
        Assert.True(SyncProbeCommand.Captured!.IncludeManaged.Value);
    }

    // R13: --managed keeps its optional value; the placeholder renders as [TRUE|FALSE] (Spectre
    // upper-cases ValueName), not the old bool-literal [FALSE].
    [Fact]
    public void Sync_ManagedOption_TemplateUsesTrueFalsePlaceholder()
    {
        var option = typeof(SyncCommand.Settings).GetProperty(nameof(SyncCommand.Settings.IncludeManaged))!
            .GetCustomAttribute<CommandOptionAttribute>()!;

        Assert.True(option.ValueIsOptional);
        Assert.Equal("TRUE|FALSE", option.ValueName);
    }

    // R12: every persisting flag's description ends with the same wording.
    [Fact]
    public void Sync_ManagedOption_DescriptionEndsWithSavedToFlowline()
    {
        var description = typeof(SyncCommand.Settings).GetProperty(nameof(SyncCommand.Settings.IncludeManaged))!
            .GetCustomAttribute<DescriptionAttribute>()!;

        Assert.EndsWith("(saved to .flowline)", description.Description);
    }

    // R12: clone's --managed persists to .flowline too, same wording rule as sync's.
    [Fact]
    public void Clone_ManagedOption_DescriptionEndsWithSavedToFlowline()
    {
        var description = typeof(CloneCommand.Settings).GetProperty(nameof(CloneCommand.Settings.IncludeManaged))!
            .GetCustomAttribute<DescriptionAttribute>()!;

        Assert.EndsWith("(saved to .flowline)", description.Description);
    }

    // Regression: --pull is a FlagValue<string> and carried [DefaultValue(false)], copied from the
    // FlagValue<bool> options above. Spectre assigns a declared default straight into FlagValue<T>.Value,
    // so binding threw InvalidCastException (Boolean -> String) before the command body ran — every
    // `configure` invocation failed with a stack trace, and the helper-level unit tests stayed green
    // because they never go through the binder. Same reason the tests above exist.
    [Fact]
    public void Configure_PullFlagBare_IsSetWithNoValue()
    {
        var app = new CommandApp<ConfigureProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["test", "--pull"]);

        Assert.Equal(0, result);
        Assert.NotNull(ConfigureProbeCommand.Captured!.Pull);
        Assert.True(ConfigureProbeCommand.Captured!.Pull.IsSet);
        Assert.Null(ConfigureProbeCommand.Captured!.Pull.Value);
    }

    [Fact]
    public void Configure_PullFlagWithValue_CarriesTheValue()
    {
        var app = new CommandApp<ConfigureProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["test", "--pull", "artifacts/solution.zip"]);

        Assert.Equal(0, result);
        Assert.True(ConfigureProbeCommand.Captured!.Pull.IsSet);
        Assert.Equal("artifacts/solution.zip", ConfigureProbeCommand.Captured!.Pull.Value);
    }

    [Fact]
    public void Configure_PullFlagAbsent_IsNotSet()
    {
        var app = new CommandApp<ConfigureProbeCommand>();
        app.Configure(config => config.PropagateExceptions());

        var result = app.Run(["test"]);

        Assert.Equal(0, result);
        Assert.NotNull(ConfigureProbeCommand.Captured!.Pull);
        Assert.False(ConfigureProbeCommand.Captured!.Pull.IsSet);
    }
}
