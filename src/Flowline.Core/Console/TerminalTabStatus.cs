using Spectre.Console;

namespace Flowline.Core.Console;

/// <summary>
/// Shows the terminal's progress indicator for a command that runs long enough to be worth reporting,
/// then clears it and marks the tab title with the outcome. A command that finishes before the reveal
/// threshold writes nothing at all.
/// </summary>
public sealed class TerminalTabStatus
{
    /// <summary>How long a command must run before the tab says anything. Long enough that --help,
    /// scaffold, and a cached status never reach it; short enough to appear before the user has
    /// switched tabs.</summary>
    public static readonly TimeSpan RevealThreshold = TimeSpan.FromSeconds(2);

    enum Stage { Idle, Shown, Done }

    readonly TerminalSignals _signals;
    readonly string _label;
    readonly ITimer? _timer;

    // The reveal timer fires on a pool thread while the command's own thread may already be
    // finishing. Guarding the stage transition *together with* its writes is what makes the pair
    // atomic: a bare compare-and-swap would let the finish clear the indicator in the window
    // between the reveal winning the swap and actually writing, which leaves it set forever.
    readonly Lock _gate = new();
    Stage _stage = Stage.Idle;

    TerminalTabStatus(TerminalSignals signals, string label)
    {
        _signals = signals;
        _label = label;
    }

    TerminalTabStatus(TerminalSignals signals, string label, TimeProvider time) : this(signals, label)
    {
        // One-shot: a due time and no period. The terminal animates its own indicator, so there is
        // no frame loop to own and nothing to tick.
        if (signals.Enabled)
            _timer = time.CreateTimer(_ => Reveal(), null, RevealThreshold, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Arms the reveal timer and returns the handle whose <see cref="Finish"/> reports the outcome.</summary>
    public static TerminalTabStatus Start(IAnsiConsole console, string label, TimeProvider? time = null)
        => new(new TerminalSignals(console), label, time ?? TimeProvider.System);

    internal static TerminalTabStatus ForTest(TerminalSignals signals, string label) => new(signals, label);

    internal static TerminalTabStatus ForTest(TerminalSignals signals, string label, TimeProvider time)
        => new(signals, label, time);

    /// <summary>Longest label written to the title. The platform title API rejects very long strings,
    /// and the label is built from raw command-line words, so it is bounded before it is written.</summary>
    const int MaxLabelLength = 200;

    /// <summary>Names the tab after the command and target the user typed: <c>flowline deploy prod</c>.
    /// The caller passes the application name so it stays the one Spectre was configured with.</summary>
    /// <remarks>
    /// Stops at the first option rather than filtering options out. Filtering would drop the flag but
    /// keep the value behind it, so <c>generate --client-secret s3cr3t</c> would put the secret in the
    /// terminal title — a value this repo already treats as sensitive and redacts from its logs.
    /// </remarks>
    public static string LabelFor(IReadOnlyList<string> args, string applicationName)
    {
        var words = args.TakeWhile(a => !a.StartsWith('-')).Take(2);
        var label = string.Join(' ', [applicationName, .. words]);
        return label.Length <= MaxLabelLength ? label : label[..MaxLabelLength];
    }

    /// <summary>Runs the command and reports its outcome on every path that unwinds — a returned exit
    /// code, and an exception a Debug build propagates instead of handling. The exit code defaults to a
    /// failure so a propagated exception is never reported as success.</summary>
    public async Task<int> RunAsync(Func<Task<int>> run)
    {
        var exitCode = (int)ExitCode.GeneralError;
        try
        {
            return exitCode = await run();
        }
        finally
        {
            Finish(exitCode);
        }
    }

    /// <summary>Shows the indicator, unless the run already finished.</summary>
    internal void Reveal()
    {
        lock (_gate)
        {
            if (_stage != Stage.Idle) return;
            _stage = Stage.Shown;
            _signals.ShowProgress();
            _signals.SetTitle(_label);
        }
    }

    /// <summary>Clears the indicator and marks the title, but only if the indicator was ever shown.
    /// Safe to call more than once and on every exit path.</summary>
    public void Finish(int exitCode)
    {
        _timer?.Dispose();

        lock (_gate)
        {
            var previous = _stage;
            _stage = Stage.Done;
            if (previous != Stage.Shown) return; // Idle: nothing was written, so nothing to undo.

            _signals.ClearProgress();
            _signals.SetTitle($"{Marker(exitCode)} {_label}");
        }
    }

    // PartialSuccess and Inconclusive are deliberately not pass/fail: a deploy that landed but left
    // orphans behind, or a check that could not finish, is not the same as a deploy that failed. They
    // share the cancelled marker rather than reporting a run that worked as an outright failure.
    static string Marker(int exitCode) => exitCode switch
    {
        0 => FlowlineTheme.OkPrefix,
        (int)ExitCode.Cancelled or (int)ExitCode.PartialSuccess or (int)ExitCode.Inconclusive
            => FlowlineTheme.WarningPrefix,
        _ => FlowlineTheme.ErrorPrefix,
    };
}
