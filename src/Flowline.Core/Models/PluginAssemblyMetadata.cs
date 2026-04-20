namespace Flowline.Core.Models;

public record PluginAssemblyMetadata(
    string Name,
    string FullName,
    byte[] Content,
    string Version,
    List<PluginTypeMetadata> Plugins,
    List<CustomApiMetadata> CustomApis);
