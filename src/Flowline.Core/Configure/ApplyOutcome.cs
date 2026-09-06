namespace Flowline.Core.Configure;

/// <summary>Everything one apply run did, and the exit code that follows from it.</summary>
/// <param name="Components">Per-component outcomes, in the order they were attempted.</param>
/// <param name="Undeclared">
/// Solution components in the covered classes the file does not name (R9). A warning only — the file is a
/// partial declaration, so not naming a component is a legitimate choice, not an error.
/// </param>
public sealed record ApplyOutcome(
    IReadOnlyList<ComponentOutcome> Components,
    IReadOnlyList<string> Undeclared)
{
    /// <summary>Components whose declared state was written.</summary>
    public int Applied => Components.Count(c => c.Outcome == ComponentOutcomeKind.Applied);

    /// <summary>Components already in their declared state.</summary>
    public int Unchanged => Components.Count(c => c.Outcome == ComponentOutcomeKind.Unchanged);

    /// <summary>Components the file names that the target does not hold.</summary>
    public int Skipped => Components.Count(c => c.Outcome == ComponentOutcomeKind.Skipped);

    /// <summary>Components Dataverse refused.</summary>
    public int Failed => Components.Count(c => c.Outcome == ComponentOutcomeKind.Failed);

    /// <summary>
    /// The exit code this run returns (KTD1).
    /// </summary>
    /// <remarks>
    /// Precedence matches deploy's post-import resolution so 18 and 19 mean the same thing in both commands:
    /// a failure outranks an inconclusive result.
    ///
    /// <see cref="ExitCode.PartialSuccess"/> covers both some-failed and all-failed. The recovery is the same
    /// either way — fix the cause and re-run, which R6 makes safe — so a separate code would buy no distinct
    /// action while permanently widening a published contract. The counts carry the difference instead.
    ///
    /// An all-skipped run compared nothing, so it is not a pass signal (<see cref="ExitCode.Inconclusive"/>).
    /// That holds under dry-run too: a dry run that matched no component is exactly the wrong-file or
    /// wrong-environment case worth catching, and reporting success there would make the preflight useless
    /// (KTD11).
    /// </remarks>
    public ExitCode ExitCode
    {
        get
        {
            if (Failed > 0) return ExitCode.PartialSuccess;
            if (Components.Count > 0 && Skipped == Components.Count) return ExitCode.Inconclusive;
            return ExitCode.Success;
        }
    }

    /// <summary>
    /// The one summary line every run ends with (R11a).
    /// </summary>
    /// <remarks>
    /// This is what carries the information the exit code deliberately drops — an operator or an agent
    /// reading only the code cannot tell one failed component from every component failing. Names appear on
    /// their own lines elsewhere; R10a's opacity covers values, never names or counts.
    /// </remarks>
    public string SummaryLine() =>
        $"{Applied} applied, {Unchanged} unchanged, {Skipped} skipped, {Failed} failed, {Undeclared.Count} undeclared";
}
