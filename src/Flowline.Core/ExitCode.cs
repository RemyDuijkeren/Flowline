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

    /// <summary>A file the command reads or writes is missing or malformed: .flowline, or an MSBuild solution file (.sln/.slnx). Check the file named in the error output is present and valid.</summary>
    ConfigInvalid = 11,

    /// <summary>Uncommitted git changes block the operation. Commit or stash changes first.</summary>
    DirtyWorkingDirectory = 12,

    /// <summary>dotnet build or PAC pack failed. Fix errors in Plugins/ and retry.</summary>
    BuildFailed = 13,

    /// <summary>
    /// Version conflict with target environment. Add --force to overwrite.
    /// Reserved: no throw site yet, but published as part of the agent-facing exit-code contract
    /// (wiki 11-AI-Agents, plugin/skills/flowline/SKILL.md), so the value stays allocated.
    /// </summary>
    VersionConflict = 14,

    /// <summary>Validation failed: drift detected, missing dependencies, or schema mismatch. Check error output.</summary>
    ValidationFailed = 15,

    /// <summary>An operation timed out: a Dataverse request got no response, or the PAC CLI 60-minute operation limit was exceeded. The write may still have landed — re-run the command to check and finish, or check environment health.</summary>
    Timeout = 16,

    /// <summary>Destructive or overwriting operation requires --force in non-interactive mode.</summary>
    ForceRequired = 17,

    /// <summary>Deploy completed but orphan cleanup failed for some components. Check output for items to remove manually via maker portal.</summary>
    PartialSuccess = 18,

    /// <summary>Check could not run to completion — an empty-input guard skipped the comparison (e.g. no local or no live components), or a deploy verification step couldn't finish (e.g. a locked directory or a Dataverse query fault). Not a pass/fail signal; investigate the printed reason before trusting the result.</summary>
    Inconclusive = 19,

    /// <summary>A file already occupies a path the command would write to, and the command will not overwrite it. Distinct from <see cref="ConfigInvalid"/>: nothing is missing or malformed — something valid is in the way. Move or remove the file named in the error output, or run the command somewhere else.</summary>
    WriteTargetOccupied = 20,

    /// <summary>
    /// Deploy completed but a plug-in package holds an assembly with no registration in the target, or one
    /// registered with no plugin types. Create the pluginassembly record under that package with sandbox
    /// isolation, then deploy again so the content write populates its plugin types. Repeats on every later
    /// deploy until that record exists.
    /// </summary>
    AssemblyNotRegistered = 21,

    /// <summary>Operation cancelled by user (Ctrl+C / SIGINT). Follows de facto convention 128+2=130.</summary>
    Cancelled = 130,
}
