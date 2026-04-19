namespace Flowline.Core.Models;

public record PluginAssemblyMetadata(
    string Name,
    string FullName,
    byte[] Content,
    string Version,
    IsolationMode IsolationMode,
    List<PluginTypeMetadata> Plugins);

public enum IsolationMode
{
    None = 1,
    Sandbox = 2
}
