namespace Flowline.Core.Models;

public record PluginAssemblyMetadata(
    string Name,
    string FullName,
    byte[] Content,
    string Hash,
    string Version,
    string? PublicKeyToken,
    string Culture,
    List<PluginTypeMetadata> Plugins);
