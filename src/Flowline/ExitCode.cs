namespace Flowline;

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

    /// <summary>Resource not found: solution not in Dataverse or repo. Verify solution name matches .flowline config.</summary>
    NotFound = 3,

    /// <summary>Not authenticated. Run: pac auth create --environment &lt;url&gt;</summary>
    NotAuthenticated = 4,

    // 5 intentionally unused — no forbidden/insufficient-permissions concept in Flowline's command surface.

    /// <summary>Dataverse environment unreachable. Check environment URL in .flowline.</summary>
    ConnectionFailed = 10,

    /// <summary>.flowline config missing or malformed. Check .flowline exists and is valid.</summary>
    ConfigInvalid = 11,

    /// <summary>Uncommitted git changes block the operation. Commit or stash changes first.</summary>
    DirtyWorkingDirectory = 12,

    /// <summary>dotnet build or PAC pack failed. Fix errors in Plugins/ and retry.</summary>
    BuildFailed = 13,

    /// <summary>Version conflict with target environment. Add --force to overwrite.</summary>
    VersionConflict = 14,

    /// <summary>Validation failed: drift detected, missing dependencies, or schema mismatch. Check error output.</summary>
    ValidationFailed = 15,

    /// <summary>PAC CLI 60-minute operation limit exceeded. Retry or check environment health.</summary>
    Timeout = 16,

    /// <summary>Destructive or overwriting operation requires --force in non-interactive mode.</summary>
    ForceRequired = 17,

    /// <summary>Operation cancelled by user (Ctrl+C / SIGINT). Follows de facto convention 128+2=130.</summary>
    Cancelled = 130,
}
