namespace Flowline.Core;

/// <summary>
/// Recognizes a Dataverse request timeout inside an exception chain and words the report for it.
/// The SOAP channel's shape is confirmed — a <see cref="TimeoutException"/>. The cancelled-task
/// shape is an assumption about the HttpClient path, not verified SDK behavior: an
/// <see cref="OperationCanceledException"/> nobody asked for is treated as a timeout because
/// Flowline creates no cancellation source of its own beyond Ctrl+C. A timeout is not a Flowline
/// bug, so the CLI reports <see cref="ExitCode.Timeout"/> and a next step instead of a stack trace.
/// </summary>
public static class DataverseTimeout
{
    /// <summary>What the user reads. Deliberately does not say "failed" — a client-side timeout
    /// means no answer arrived, not that the server rejected the write.</summary>
    public const string Message = "Dataverse didn't respond in time — the request timed out.";

    /// <summary>
    /// True when <paramref name="exception"/>, or anything it wraps, is a request timeout.
    /// </summary>
    /// <param name="userCancelled">
    /// Whether the Ctrl+C token fired. A cancelled task is only a timeout when the user didn't ask
    /// for it — the exception type alone can't tell the two apart, since the HttpClient path throws
    /// <see cref="TaskCanceledException"/> for both.
    /// </param>
    public static bool Matches(Exception? exception, bool userCancelled)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;

            if (current is OperationCanceledException && !userCancelled)
                return true;

            // InnerException only exposes the first of an AggregateException's children, so the
            // walk above would miss a timeout parked in any of the others.
            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(inner => Matches(inner, userCancelled)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The recovery line. Every command that can hit this is idempotent, so re-running is both the
    /// check and the fix.
    /// </summary>
    /// <param name="command">
    /// The first token as typed, or null when it can't be determined. Single-token commands are all
    /// that reach here — the nested ones (`sln add`) touch no Dataverse — so the first token is the
    /// whole command. Revisit if a subcommand ever writes to an environment.
    /// </param>
    public static string NextStep(string? command)
    {
        var rerun = string.IsNullOrWhiteSpace(command) || command.StartsWith('-')
            ? "Re-run the command"
            : $"Re-run 'flowline {command}'";

        return $"It may still have applied the change. {rerun} to check and finish.";
    }
}
