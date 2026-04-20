using System.Reflection;
using System.Runtime.InteropServices;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class AssemblyAnalysisService(IFlowlineOutput output)
{
    private static readonly string[] MessageNames =
        Enum.GetNames<MessageName>().OrderByDescending(n => n.Length).ToArray();

    // Messages that support entity images.
    // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images
    private static readonly HashSet<string> ImageSupportedMessages =
    [
        "Assign", "Create", "Delete", "DeliverIncoming", "DeliverPromote",
        "Merge", "Route", "Send", "SetState", "Update"
    ];

    public PluginAssemblyMetadata Analyze(string dllPath)
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var paths = Directory.GetFiles(runtimeDir, "*.dll").ToList();

        var assemblyDir = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrWhiteSpace(assemblyDir) && Directory.Exists(assemblyDir))
        {
            var parentDir = Directory.GetParent(assemblyDir)?.FullName;
            if (parentDir != null && Directory.Exists(parentDir))
            {
                foreach (var file in Directory.EnumerateFiles(parentDir, "*.dll", SearchOption.AllDirectories))
                    paths.Add(file);
            }
        }

        paths.Add(dllPath);
        var resolver = new PathAssemblyResolver(paths);
        using var mlc = new MetadataLoadContext(resolver);

        var assembly = mlc.LoadFromAssemblyPath(dllPath);
        var assemblyName = assembly.GetName();
        var content = File.ReadAllBytes(dllPath);

        output.Info($"Loaded assembly {assemblyName.Name}");

        var plugins = new List<PluginTypeMetadata>();
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }))
        {
            var isPlugin = IsDerivedFrom(type, "Microsoft.Xrm.Sdk.IPlugin");
            var isWorkflow = IsDerivedFrom(type, "System.Activities.CodeActivity");

            if (!isPlugin && !isWorkflow) continue;

            if (isPlugin) output.Verbose($"Found plugin {type.FullName}");
            if (isWorkflow) output.Verbose($"Found workflow {type.FullName}");

            var steps = new List<PluginStepMetadata>();
            if (isPlugin)
            {
                var step = TryBuildStep(type);
                if (step != null)
                {
                    output.Verbose($"with plugin step {step.Name}");
                    foreach (var warning in step.Warnings)
                        output.Info($"[yellow]Warning:[/] {warning}");
                    steps.Add(step);
                }
            }
            // workflows don't register plugin steps

            plugins.Add(new PluginTypeMetadata(type.Name, type.FullName!, isWorkflow, steps));
        }

        return new PluginAssemblyMetadata(
            assemblyName.Name!,
            assemblyName.FullName,
            content,
            assemblyName.Version!.ToString(),
            plugins);
    }

    private static PluginStepMetadata? TryBuildStep(Type type)
    {
        var entityAttr = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Flowline.Attributes.EntityAttribute");
        if (entityAttr == null) return null;

        var logicalName = (string)entityAttr.ConstructorArguments[0].Value!;
        var order = 1;
        var configuration = (string?)null;
        foreach (var arg in entityAttr.NamedArguments)
        {
            if (arg.MemberName == "Order") order = Convert.ToInt32(arg.TypedValue.Value);
            else if (arg.MemberName == "Configuration") configuration = (string?)arg.TypedValue.Value;
        }

        if (!TryParseClassName(type.Name, out var message, out var stage, out var mode))
            return null;

        ValidateExecutionMode(type.Name, stage, mode);

        var filteringAttributes = ReadFilterAttributes(type);
        var images = ReadImageAttributes(type);
        ValidateImages(type.Name, message, stage, images);
        var warnings = BuildImageWarnings(type.Name, images);

        return new PluginStepMetadata(
            $"{type.FullName}: {message} of {logicalName}",
            message, logicalName, stage, mode, order, filteringAttributes, configuration, images, warnings);
    }

    internal static bool TryParseClassName(string className, out string message, out int stage, out int mode)
    {
        message = "";
        stage = 0;
        mode = 0;

        var name = className;
        if (name.EndsWith("Plugin", StringComparison.Ordinal))
            name = name[..^6];

        if (name.EndsWith("Async", StringComparison.Ordinal))
        {
            mode = (int)ProcessingMode.Asynchronous;
            name = name[..^5];
        }

        var messageFound = false;
        foreach (var msgName in MessageNames)
        {
            if (name.EndsWith(msgName, StringComparison.Ordinal))
            {
                message = msgName;
                name = name[..^msgName.Length];
                messageFound = true;
                break;
            }
        }
        if (!messageFound) return false;

        // Validation checked before Pre to avoid partial matches
        (string keyword, int value)[] stages =
        [
            ("Validation", (int)ProcessingStage.PreValidation),
            ("Pre",        (int)ProcessingStage.PreOperation),
            ("Post",       (int)ProcessingStage.PostOperation),
        ];

        foreach (var (keyword, value) in stages)
        {
            if (name.EndsWith(keyword, StringComparison.Ordinal))
            {
                stage = value;
                return true;
            }
        }

        return false;
    }

    private static string? ReadFilterAttributes(Type type)
    {
        var filterAttr = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Flowline.Attributes.FilterAttribute");
        if (filterAttr == null) return null;

        var attrs = ReadStringArray(filterAttr.ConstructorArguments[0]);
        return attrs.Length > 0 ? string.Join(",", attrs) : null;
    }

    // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#execution-mode
    internal static void ValidateExecutionMode(string className, int stage, int mode)
    {
        if (mode == (int)ProcessingMode.Asynchronous && stage != (int)ProcessingStage.PostOperation)
            throw new InvalidOperationException(
                $"{className}: Asynchronous execution is only available for PostOperation — " +
                $"PreValidation and PreOperation always run synchronously. Rename the class to remove 'Async' or change the stage to Post. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#execution-mode");
    }

    // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images
    internal static void ValidateImages(string className, string message, int stage, List<PluginImageMetadata> images)
    {
        if (images.Count == 0) return;

        if (!ImageSupportedMessages.Contains(message))
            throw new InvalidOperationException(
                $"{className}: entity images are not supported for the {message} message. " +
                $"Supported: Assign, Create, Delete, DeliverIncoming, DeliverPromote, Merge, Route, Send, SetState, Update. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images");

        var hasPreImage = images.Any(i => i.ImageType == (int)ImageType.PreImage);
        var hasPostImage = images.Any(i => i.ImageType == (int)ImageType.PostImage);

        if (hasPreImage && message == "Create")
            throw new InvalidOperationException(
                $"{className}: [PreImage] cannot be used on a Create step — the record does not exist before Create. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images");

        if (hasPostImage && message == "Delete")
            throw new InvalidOperationException(
                $"{className}: [PostImage] cannot be used on a Delete step — the record no longer exists after Delete. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images");

        if (hasPostImage && stage != (int)ProcessingStage.PostOperation)
            throw new InvalidOperationException(
                $"{className}: [PostImage] is only available in PostOperation — the record state is unknown before the operation completes. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images");
    }

    // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images
    private static List<string> BuildImageWarnings(string className, List<PluginImageMetadata> images)
    {
        var warnings = new List<string>();
        foreach (var image in images)
        {
            if (image.Attributes != null) continue;
            var attrName = image.ImageType == (int)ImageType.PreImage ? "PreImage" : "PostImage";
            warnings.Add(
                $"{className}: [{attrName}] has no attribute filter — all columns will be fetched, which negatively impacts performance. " +
                $"Specify only the columns your plugin requires. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images");
        }
        return warnings;
    }

    private static List<PluginImageMetadata> ReadImageAttributes(Type type)
    {
        var images = new List<PluginImageMetadata>();
        foreach (var imageData in type.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName is
                "Flowline.Attributes.PreImageAttribute" or
                "Flowline.Attributes.PostImageAttribute"))
        {
            images.Add(BuildImageMetadata(imageData));
        }
        return images;
    }

    private static PluginImageMetadata BuildImageMetadata(CustomAttributeData imageData)
    {
        int imageType = imageData.AttributeType.Name switch
        {
            "PreImageAttribute" => (int)ImageType.PreImage,
            _ => (int)ImageType.PostImage
        };

        string alias = imageType switch { 0 => "preimage", 1 => "postimage", _ => "image" };
        string name = imageType switch { 0 => "Pre Image", 1 => "Post Image", _ => "Image" };

        var attrs = imageData.ConstructorArguments.Count > 0
            ? ReadStringArray(imageData.ConstructorArguments[0])
            : [];

        foreach (var namedArg in imageData.NamedArguments)
        {
            if (namedArg.MemberName == "Alias") name = alias = (string)namedArg.TypedValue.Value!;
        }

        if (string.IsNullOrWhiteSpace(alias))
            throw new InvalidOperationException(
                $"[{imageData.AttributeType.Name.Replace("Attribute", "")}] on a plugin has an empty Name. Name cannot be blank.");

        return new PluginImageMetadata(name, alias, imageType, attrs.Length > 0 ? string.Join(",", attrs) : null);
    }

    private static string[] ReadStringArray(CustomAttributeTypedArgument arg)
    {
        if (arg.Value is not IEnumerable<CustomAttributeTypedArgument> elements)
            return [];
        return [.. elements.Select(e => (string)e.Value!)];
    }

    private bool IsDerivedFrom(Type type, string targetTypeName)
    {
        var current = type;
        while (current != null)
        {
            if (current.FullName == targetTypeName) return true;
            if (current.GetInterfaces().Any(i => i.FullName == targetTypeName)) return true;
            current = current.BaseType;
        }
        return false;
    }
}
