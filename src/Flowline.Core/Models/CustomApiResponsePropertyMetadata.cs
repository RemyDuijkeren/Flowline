namespace Flowline.Core.Models;

public record CustomApiResponsePropertyMetadata(
    string UniqueName,
    string? DisplayName,
    string? Description,
    int Type,
    string? EntityName);
