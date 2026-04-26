namespace Flowline.Core.Models;

public record RequestParameterMetadata(
    string UniqueName,
    string? Name,
    string? DisplayName,
    string? Description,
    int Type,
    bool IsOptional,
    string? EntityName);
