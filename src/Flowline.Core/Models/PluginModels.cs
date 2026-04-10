namespace Flowline.Core.Models;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class StepAttribute : Attribute
{
    public string Message { get; }
    public string EntityName { get; }
    public int Stage { get; } // 10 = Pre-validation, 20 = Pre-operation, 40 = Post-operation
    public int Mode { get; } // 0 = Synchronous, 1 = Asynchronous
    public int Order { get; set; } = 1;
    public string? FilteringAttributes { get; set; }
    public string? Configuration { get; set; }

    public StepAttribute(string message, string entityName, int stage = 40, int mode = 0)
    {
        Message = message;
        EntityName = entityName;
        Stage = stage;
        Mode = mode;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ImageAttribute : Attribute
{
    public string Name { get; }
    public string Alias { get; }
    public int ImageType { get; } // 0 = Pre-Image, 1 = Post-Image, 2 = Both
    public string? Attributes { get; set; }

    public ImageAttribute(string name, string alias, int imageType = 1)
    {
        Name = name;
        Alias = alias;
        ImageType = imageType;
    }
}

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
    List<PluginStepMetadata> Steps);

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
