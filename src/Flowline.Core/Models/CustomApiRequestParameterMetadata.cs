namespace Flowline.Core.Models;

public record CustomApiRequestParameterMetadata(
    string UniqueName,
    string? DisplayName,
    string? Description,
    int Type,
    bool IsOptional,
    string? EntityName);
