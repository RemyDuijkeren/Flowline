namespace Flowline.Core.Models;

public enum IsolationMode
{
    None = 1,
    Sandbox = 2
}


public record PluginAssemblyMetadata(
    string Name,
    string FullName,
    byte[] Content,
    string Version,
    IsolationMode IsolationMode,
    List<PluginTypeMetadata> Plugins);

public record PluginTypeMetadata(
    string Name,
    string FullName,
    List<PluginStepMetadata> Steps)
{
}

public record PluginStepMetadata(
    string Name,
    string Message,
    string EntityName,
    int Stage,
    int Mode,
    int Order,
    string? FilteringAttributes,
    string? Configuration,
    List<PluginImageMetadata> Images);

public record PluginImageMetadata(
    string Name,
    string Alias,
    int ImageType,
    string? Attributes);
