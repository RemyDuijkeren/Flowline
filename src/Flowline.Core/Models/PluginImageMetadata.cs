namespace Flowline.Core.Models;

public record PluginImageMetadata(
    string Name,
    string Alias,
    int ImageType,
    string? Attributes);
