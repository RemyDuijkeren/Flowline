namespace Flowline.Core.Deploy;

// Deliberately excludes RequiredComponent.Id/ParentId/ParentDisplayName/ParentSchemaName — the target
// doesn't reliably populate them (see MissingComponentCheckService.MapMissingComponents), and omitting
// Id entirely guarantees a bare GUID can never reach the terminal or the report file.
public sealed record MissingComponentResult(
    string? RequiredSchemaName,
    string? RequiredDisplayName,
    string? RequiredSolution,
    int RequiredComponentType,
    string? DependentSchemaName,
    string? DependentDisplayName);
