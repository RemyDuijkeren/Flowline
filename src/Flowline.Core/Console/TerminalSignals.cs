using Spectre.Console;

namespace Flowline.Core.Console;

/// <summary>
/// Writes the terminal's own progress state and the tab title. Decides once whether Flowline may
/// emit terminal control sequences at all, then emits them or stays silent.
/// </summary>
/// <remarks>
/// The escape sequences go to <b>standard error</b>, not standard output, so they cannot interleave
/// with a Spectre live region (<see cref="SpinnerExtensions"/>) rendering on standard output. Keeping
/// them out of a piped capture is already handled by the gate: redirecting standard output turns
/// <c>Interactive</c> and <c>Ansi</c> off, so nothing is written in the first place.
/// <para>
/// Reading <c>Profile.Capabilities.Ansi</c> is also what makes the stderr write safe on Windows. Virtual
/// terminal processing is a per-handle console mode, off by default under classic conhost; measured on
/// Windows 11, that property access enables it on <i>both</i> the stdout and stderr handles (mode
/// 0x0003 -> 0x000F), and under Windows Terminal both handles already have it. So the gate both decides
/// and enables, and it always runs before the first write.
/// </para>
/// </remarks>
internal sealed class TerminalSignals
{
    // OSC 9;4;<state>;<progress> BEL. State 3 is indeterminate, state 0 removes the indicator.
    // https://learn.microsoft.com/en-us/windows/terminal/tutorials/progress-bar-sequences
    const string ProgressIndeterminate = "\e]9;4;3;0\a";
    const string ProgressRemove = "\e]9;4;0;0\a";

    readonly bool _enabled;
    readonly TextWriter _escapes;
    readonly Action<string> _setTitle;

    /// <summary>Production wiring: gate on the real console, write to real standard error.</summary>
    internal TerminalSignals(IAnsiConsole console)
        : this(console, System.Console.IsErrorRedirected, System.Console.Error, SetConsoleTitle) { }

    internal TerminalSignals(IAnsiConsole console, bool errorRedirected, TextWriter escapes, Action<string> setTitle)
    {
        // Read once. A capability change mid-run must not let the clear fire while the show didn't,
        // or the reverse — that would leave the indicator running for the rest of the session.
        _enabled = console.Profile.Capabilities is { Interactive: true, Ansi: true } && !errorRedirected;
        _escapes = escapes;
        _setTitle = setTitle;
    }

    /// <summary>Whether anything will be written at all. False under CI, piping, and dumb terminals.</summary>
    public bool Enabled => _enabled;

    public void ShowProgress() => Write(ProgressIndeterminate);

    public void ClearProgress() => Write(ProgressRemove);

    public void SetTitle(string title)
    {
        if (_enabled) _setTitle(title);
    }

    void Write(string sequence)
    {
        if (!_enabled) return;

        try
        {
            _escapes.Write(sequence);
            _escapes.Flush();
        }
        catch (IOException) { } // Intentional: a closed stream must not fail the command.
    }

    static void SetConsoleTitle(string title)
    {
        // The title is best-effort (R4). There is no portable getter — it throws on Unix — so the
        // previous title can't be restored either; the outcome marker is left standing on purpose.
        // ArgumentOutOfRangeException is in the list because the setter rejects a very long title on
        // Windows, and this runs on the reveal timer's pool thread where an escape would kill the
        // process rather than reach the command's exception handler.
        try
        {
            System.Console.Title = title;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException) { }
    }
}
