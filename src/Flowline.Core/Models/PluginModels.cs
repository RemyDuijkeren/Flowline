namespace Flowline.Core.Models;

// TO BE REMOVED => use EntityAttribute and classname by convention.
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
