namespace Flowline.Core.Models;

public record PluginStepMetadata(
    string Name,
    string Message,
    string? TableName,
    int Stage,
    int Mode,
    int Order,
    string? FilteringColumns,
    string? Configuration,
    List<PluginImageMetadata> Images,
    List<string> Warnings,
    string? SecondaryTable = null,
    bool AsyncAutoDelete = false,
    Guid? RunAs = null,
    string? Description = null);