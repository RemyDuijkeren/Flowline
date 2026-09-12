using Spectre.Console;

namespace Flowline.Commands;

/// <summary>
/// Esc at a prompt: the operator backed out of the run, not just out of the question.
/// </summary>
/// <remarks>
/// Derives from <see cref="OperationCanceledException"/> so the paths that already understand
/// cancellation keep working without being told about prompts. The capture sweep in particular rethrows
/// cancellation rather than treating it as one environment failing, which is what stops Esc in the middle
/// of a three-environment run from quietly moving on to the next one.
///
/// It needs its own arm in the top-level handler regardless, and that arm has to sit above the timeout
/// check: <c>DataverseTimeout.Matches</c> reads any <see cref="OperationCanceledException"/> raised
/// without the Ctrl+C token as a timed-out request, so Esc would otherwise be reported as an unreachable
/// environment.
/// </remarks>
public sealed class PromptCancelledException() : OperationCanceledException("Cancelled at a prompt.");

/// <summary>
/// Prompts that Esc backs out of.
/// </summary>
/// <remarks>
/// Spectre carries a cancel result on its selection prompts, which Esc returns instead of a choice. It
/// carries nothing equivalent on a text or confirmation prompt, so those keep their own way out: a blank
/// answer, or no.
///
/// The sentinel is turned into an exception here rather than handed back to each caller. Every one of
/// them would otherwise need a "did they cancel" branch alongside its real answer, and a caller that
/// forgot would read a cancel as a choice.
/// </remarks>
internal static class CancellablePrompt
{
    /// <summary>Asks, or throws when the answer was Esc.</summary>
    public static async Task<T> AskAsync<T>(IAnsiConsole console, SelectionPrompt<T> prompt, CancellationToken ct)
        where T : class
    {
        prompt.AddCancelResult(() => null!);

        return await console.PromptAsync(prompt, ct) ?? throw new PromptCancelledException();
    }

    /// <inheritdoc cref="AskAsync{T}(IAnsiConsole, SelectionPrompt{T}, CancellationToken)"/>
    public static async Task<IReadOnlyList<T>> AskAsync<T>(
        IAnsiConsole console, MultiSelectionPrompt<T> prompt, CancellationToken ct)
    {
        prompt.AddCancelResult(() => null!);

        return await console.PromptAsync(prompt, ct) ?? throw new PromptCancelledException();
    }
}
