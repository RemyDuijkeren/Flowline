namespace Flowline.Core.Models;

public record ResponsePropertyMetadata(
    string UniqueName,
    string? Name,
    string? DisplayName,
    string? Description,
    int Type,
    string? EntityName);
