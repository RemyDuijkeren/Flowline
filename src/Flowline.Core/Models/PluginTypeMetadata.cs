namespace Flowline.Core.Models;

public record PluginTypeMetadata(
    string Name,
    string FullName,
    List<PluginStepMetadata> Steps,
    List<CustomApiMetadata> CustomApis,
    bool IsWorkflow = false,
    bool IsCustomApi = false);
