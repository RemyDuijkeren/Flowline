namespace Flowline.Services;

sealed record RunLogRecord(
    DateTimeOffset Timestamp,
    string CommandName,
    string ArgsRedacted,
    int ExitCode,
    long DurationMs,
    string FlowlineVersion,
    Dictionary<string, string?> ToolVersions,
    string LogFilePath,
    string? ExceptionType,
    string? ExceptionMessage,
    string? ExceptionStackTrace,
    string[]? VerboseOutput
);
