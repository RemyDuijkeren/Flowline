namespace Flowline.Core.Models;

public record PluginTypeMetadata(
    string Name,
    string FullName,
    bool IsWorkflow,
    List<PluginStepMetadata> Steps);
