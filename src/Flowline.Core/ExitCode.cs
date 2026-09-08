namespace Flowline.Core;

/// <summary>
/// Process exit codes returned by Flowline commands.
/// Treat as a stable public API — agents and scripts pattern-match on these values.
/// Codes 3 and 4 follow de facto CLI conventions (curl, git, etc.).
/// </summary>
public enum ExitCode
{
    /// <summary>Command completed successfully.</summary>
    Success = 0,

    /// <summary>Unexpected or unhandled error. Check error output.</summary>
    GeneralError = 1,

    // 2 intentionally unused — Spectre.Console handles argument validation errors internally.

    /// <summary>Resource not found: a Dataverse solution, or a local file the command needs (project file, solution file). Check the name or path named in the error output.</summary>
    NotFound = 3,

    /// <summary>Not authenticated. Run: pac auth create --environment &lt;url&gt;</summary>
    NotAuthenticated = 4,

    // 5 intentionally unused — no forbidden/insufficient-permissions concept in Flowline's command surface.

    /// <summary>Dataverse environment unreachable. Check environment URL in .flowline.</summary>
    ConnectionFailed = 10,

    /// <summary>The command could not establish what to work on from its inputs: a file it reads or writes is missing or malformed (.flowline, an MSBuild solution file (.sln/.slnx), or a settings file), a role keyword can't be resolved because there is no project to resolve it from, or the solution can't be identified outside a project. The error names what was missing: check the file it names, or supply the flag it asks for.</summary>
    ConfigInvalid = 11,

    /// <summary>Uncommitted git changes block the operation. Commit or stash changes first.</summary>
    DirtyWorkingDirectory = 12,

    /// <summary>dotnet build or PAC pack failed. Fix errors in Plugins/ and retry.</summary>
    BuildFailed = 13,

    /// <summary>
    /// Version conflict with target environment. Add --force to overwrite.
    /// Reserved: published in the agent-facing exit-code contract (wiki 11-AI-Agents,
    /// plugin/skills/flowline/SKILL.md) so the value stays allocated, but never thrown.
    /// </summary>
    VersionConflict = 14,

    /// <summary>Validation failed: drift detected, missing dependencies, schema mismatch, or flags that contradict each other. Check error output.</summary>
    ValidationFailed = 15,

    /// <summary>An operation timed out: a Dataverse request got no response, or the PAC CLI 60-minute operation limit was exceeded. The write may still have landed — re-run the command to check and finish, or check environment health.</summary>
    Timeout = 16,

    /// <summary>Destructive or overwriting operation requires --force in non-interactive mode.</summary>
    ForceRequired = 17,

    /// <summary>The run finished but part of it failed: deploy's orphan cleanup couldn't remove some components, or configure couldn't apply some of them. Covers one failure and every failure alike — the printed counts tell those apart, and the recovery is the same either way. Check output for what to fix, then re-run.</summary>
    PartialSuccess = 18,

    /// <summary>Check could not run to completion — an empty-input guard skipped the comparison (e.g. no local or no live components), a deploy verification step couldn't finish (e.g. a locked directory or a Dataverse query fault), or every component a settings file declared was absent from the target, which usually means the wrong file or the wrong environment. Not a pass/fail signal; investigate the printed reason before trusting the result.</summary>
    Inconclusive = 19,

    /// <summary>A file already occupies a path the command would write to, and the command will not overwrite it. Distinct from <see cref="ConfigInvalid"/>: nothing is missing or malformed — something valid is in the way. Move or remove the file named in the error output, or run the command somewhere else.</summary>
    WriteTargetOccupied = 20,

    /// <summary>
    /// Deploy completed but a plug-in package holds an assembly with no registration in the target, or one
    /// registered with no plugin types. On an unmanaged target deploy creates the missing record before the
    /// import, so reaching this code means that repair was refused or failed: a managed target, where
    /// Flowline never writes the record, or a create Dataverse rejected. Create the pluginassembly record
    /// under that package with sandbox isolation and the assembly's own version, culture and public key
    /// token, then deploy again. Repeats on every later deploy until that record exists.
    /// </summary>
    AssemblyNotRegistered = 21,

    /// <summary>
    /// Changes were found. <b>Not a failure</b> — the command ran to completion and the comparison
    /// succeeded. Only returned when the caller opts in with <c>--exit-code</c>, so a run without that
    /// option reports changes and still exits <see cref="Success"/>. Gate on this to branch on "is there
    /// anything to sync/deploy/drift" without parsing output. Shared by <c>diff</c> and <c>drift</c>.
    /// </summary>
    ChangesFound = 22,

    /// <summary>Operation cancelled by user (Ctrl+C / SIGINT). Follows de facto convention 128+2=130.</summary>
    Cancelled = 130,
}
