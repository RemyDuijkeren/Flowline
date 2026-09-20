using System.Diagnostics;
using Flowline.Core.Diagnostics;
using Spectre.Console;

namespace Flowline.Core.Console;

public static class SpinnerExtensions
{
    internal static readonly Spinner s_spinnerType = Spinner.Known.Arrow3; // Arrow3 > Default > Star > BouncingBar ≈ Aesthetic
    internal static readonly Color s_spinnerColor = Color.Turquoise2; //Turquoise2, Plum4, DarkMagenta, DarkMagenta_1;

    extension(Status status)
    {
        /// <summary>
        /// Applies the Flowline spinner style to a <see cref="Status"/> context and returns a
        /// <see cref="FlowlineStatus"/> whose <c>StartAsync</c> / <c>Start</c> overloads
        /// automatically color the status text to match the spinner.
        /// Use as <c>AnsiConsole.Status().FlowlineSpinner().StartAsync(...)</c>.
        /// </summary>
        public FlowlineStatus FlowlineSpinner()
            => new(status.Spinner(s_spinnerType)
                         .SpinnerStyle(new Style(foreground: s_spinnerColor)));
    }

    extension(StatusContext ctx)
    {
        /// <summary>
        /// Retitles a running spinner for the next phase of a multi-step wait, keeping the Flowline
        /// spinner colour that <see cref="FlowlineStatus"/> applied to the opening label — a bare
        /// <c>ctx.Status(...)</c> drops it.
        /// </summary>
        public void Phase(string statusText)
            => ctx.Status($"[{s_spinnerColor}]{statusText}[/]");
    }

    extension<T>(Task<T> task)
    {
        /// <summary>
        /// Awaits a <see cref="Task{T}"/> while showing the Flowline spinner.
        /// Use instead of <c>.Spinner()</c> directly so spinner appearance is defined in one place.
        /// </summary>
        public Task<T> FlowlineSpinner()
            => task.Spinner(s_spinnerType, new Style(foreground: s_spinnerColor));
    }

    extension(Task task)
    {
        /// <inheritdoc cref="FlowlineSpinner{T}"/>
        public Task FlowlineSpinner()
            => task.Spinner(s_spinnerType, new Style(foreground: s_spinnerColor));
    }
}

public readonly struct FlowlineStatus(Status status)
{
    public async Task StartAsync(string statusText, Func<StatusContext, Task> action)
    {
        using var stage = FlowlineStage.Start(statusText);
        try { await status.StartAsync($"[{SpinnerExtensions.s_spinnerColor}]{statusText}[/]", action); }
        catch (Exception ex) { stage.Failed(ex); throw; }
    }

    public async Task<T> StartAsync<T>(string statusText, Func<StatusContext, Task<T>> action)
    {
        using var stage = FlowlineStage.Start(statusText);
        try { return await status.StartAsync($"[{SpinnerExtensions.s_spinnerColor}]{statusText}[/]", action); }
        catch (Exception ex) { stage.Failed(ex); throw; }
    }

    public void Start(string statusText, Action<StatusContext> action)
    {
        using var stage = FlowlineStage.Start(statusText);
        try { status.Start($"[{SpinnerExtensions.s_spinnerColor}]{statusText}[/]", action); }
        catch (Exception ex) { stage.Failed(ex); throw; }
    }

    public T Start<T>(string statusText, Func<StatusContext, T> action)
    {
        using var stage = FlowlineStage.Start(statusText);
        try { return status.Start($"[{SpinnerExtensions.s_spinnerColor}]{statusText}[/]", action); }
        catch (Exception ex) { stage.Failed(ex); throw; }
    }
}

/// <summary>
/// A named span around one phase of a command, started from the spinner every phase already shows.
/// </summary>
/// <remarks>
/// Here rather than at each call site because the spinner is the one place every phase passes
/// through: twenty-five call sites across the commands get a stage span without any of them saying
/// so, and a phase added later is instrumented by construction.
/// </remarks>
public readonly struct FlowlineStage(Activity? activity) : IDisposable
{
    public static FlowlineStage Start(string statusText) =>
        new(FlowlineActivitySource.Source.StartActivity(NameFrom(statusText)));

    public void Failed(Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("stage.error", ex.GetType().Name);
    }

    public void Dispose() => activity?.Dispose();

    /// <summary>
    /// Derives a stable span name from a spinner label: the words before the first value it
    /// interpolates, which is where every label puts the thing being acted on.
    /// </summary>
    /// <remarks>
    /// "Packing [bold]AcmeBank[/]..." becomes "Packing". Taking the whole label instead would give
    /// every run its own span name — unaggregatable — and put a solution name in a field the scrubber
    /// does not reach, because a span's name is not one of its tags.
    /// </remarks>
    internal static string NameFrom(string statusText)
    {
        var upToFirstValue = statusText.Split('[')[0].Trim();
        var name = (upToFirstValue.Length > 0 ? upToFirstValue : statusText).TrimEnd('.', ' ');
        return name.Length > 0 ? name : "stage";
    }
}
