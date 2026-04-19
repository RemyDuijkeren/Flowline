using System.Reflection;
using System.Runtime.InteropServices;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public interface IAssemblyAnalysisService
{
    PluginAssemblyMetadata Analyze(string dllPath, IsolationMode isolationMode);
}

public class AssemblyAnalysisService : IAssemblyAnalysisService
{
    private static readonly string[] MessageNames =
        Enum.GetNames<MessageName>().OrderByDescending(n => n.Length).ToArray();

    public PluginAssemblyMetadata Analyze(string dllPath, IsolationMode isolationMode)
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

        var plugins = new List<PluginTypeMetadata>();

        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }))
        {
            var isPlugin = IsDerivedFrom(type, "Microsoft.Xrm.Sdk.IPlugin");
            var isWorkflow = IsDerivedFrom(type, "System.Activities.CodeActivity");

            if (!isPlugin && !isWorkflow) continue;

            var steps = new List<PluginStepMetadata>();

            if (isPlugin)
            {
                var step = TryBuildStep(type);
                if (step != null)
                    steps.Add(step);
            }
            // workflows don't register plugin steps

            plugins.Add(new PluginTypeMetadata(type.Name, type.FullName!, isWorkflow, steps));
        }

        return new PluginAssemblyMetadata(
            assemblyName.Name!,
            assemblyName.FullName,
            content,
            assemblyName.Version!.ToString(),
            isolationMode,
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

        var filteringAttributes = ReadFilterAttributes(type);
        var images = ReadImageAttributes(type);

        return new PluginStepMetadata(
            $"{type.FullName}: {message} of {logicalName}",
            message, logicalName, stage, mode, order, filteringAttributes, configuration, images);
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

        // Validation/Validate checked before Pre/Post to avoid partial matches
        (string keyword, int value)[] stages =
        [
            ("Validation", (int)ProcessingStage.PreValidation),
            ("Validate",   (int)ProcessingStage.PreValidation),
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

    private static List<PluginImageMetadata> ReadImageAttributes(Type type)
    {
        var images = new List<PluginImageMetadata>();
        foreach (var imageData in type.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Flowline.Attributes.ImageAttribute"))
        {
            images.Add(BuildImageMetadata(imageData));
        }
        return images;
    }

    private static PluginImageMetadata BuildImageMetadata(CustomAttributeData imageData)
    {
        string alias;
        int imageType;
        string[] attrs;

        // Distinguish (string alias, ImageType, string[]) from (ImageType, string[]) by first arg type
        if (imageData.ConstructorArguments[0].ArgumentType.FullName == "System.String")
        {
            alias = (string)imageData.ConstructorArguments[0].Value!;
            imageType = Convert.ToInt32(imageData.ConstructorArguments[1].Value);
            attrs = imageData.ConstructorArguments.Count > 2
                ? ReadStringArray(imageData.ConstructorArguments[2])
                : [];
        }
        else
        {
            imageType = Convert.ToInt32(imageData.ConstructorArguments[0].Value);
            attrs = imageData.ConstructorArguments.Count > 1
                ? ReadStringArray(imageData.ConstructorArguments[1])
                : [];
            alias = imageType switch
            {
                0 => "preimage",
                1 => "postimage",
                _ => "image"
            };
        }

        var name = imageType switch { 0 => "Pre Image", 1 => "Post Image", _ => "Image" };
        foreach (var namedArg in imageData.NamedArguments)
        {
            if (namedArg.MemberName == "Name") name = (string)namedArg.TypedValue.Value!;
        }

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
