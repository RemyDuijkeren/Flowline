using CliWrap;
using Spectre.Console;
using Spectre.Console.Extensions;

namespace Flowline.Utils;

public static class SpinnerExtensions
{
    internal static readonly Spinner SpinnerType = Spinner.Known.Default; // BoxBounce2 .Binary .Arrow3 .Dots12
    internal static readonly Color SpinnerColor = Color.CadetBlue_1; //.MediumOrchid;

    extension(Status status)
    {
        /// <summary>
        /// Applies the Flowline spinner style to a <see cref="Status"/> context and returns a
        /// <see cref="FlowlineStatus"/> whose <c>StartAsync</c> / <c>Start</c> overloads
        /// automatically color the status text to match the spinner.
        /// Use as <c>AnsiConsole.Status().FlowlineSpinner().StartAsync(...)</c>.
        /// </summary>
        public FlowlineStatus FlowlineSpinner()
            => new(status.Spinner(SpinnerType)
                         .SpinnerStyle(new Style(foreground: SpinnerColor)));
    }

    extension<T>(Task<T> task)
    {
        /// <summary>
        /// Awaits a <see cref="Task{T}"/> while showing the Flowline spinner.
        /// Use instead of <c>.Spinner()</c> directly so spinner appearance is defined in one place.
        /// </summary>
        public Task<T> FlowlineSpinner()
            => task.Spinner(SpinnerType, new Style(foreground: SpinnerColor));
    }

    extension(Task task)
    {
        /// <inheritdoc cref="FlowlineSpinner{T}"/>
        public Task FlowlineSpinner()
            => task.Spinner(SpinnerType, new Style(foreground: SpinnerColor));
    }
}

public readonly struct FlowlineStatus(Status status)
{
    public Task StartAsync(string statusText, Func<StatusContext, Task> action)
        => status.StartAsync($"[{SpinnerExtensions.SpinnerColor}]{statusText}[/]", action);

    public Task<T> StartAsync<T>(string statusText, Func<StatusContext, Task<T>> action)
        => status.StartAsync($"[{SpinnerExtensions.SpinnerColor}]{statusText}[/]", action);

    public void Start(string statusText, Action<StatusContext> action)
        => status.Start($"[{SpinnerExtensions.SpinnerColor}]{statusText}[/]", action);

    public T Start<T>(string statusText, Func<StatusContext, T> action)
        => status.Start($"[{SpinnerExtensions.SpinnerColor}]{statusText}[/]", action);
}
