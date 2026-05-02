namespace Flowline.Core.Models;

public record PluginStepMetadata(
    string Name,
    string Message,
    string? EntityName,
    int Stage,
    int Mode,
    int Order,
    string? FilteringAttributes,
    string? Configuration,
    List<PluginImageMetadata> Images,
    List<string> Warnings,
    string? SecondaryEntity = null,
    bool AsyncAutoDelete = false);