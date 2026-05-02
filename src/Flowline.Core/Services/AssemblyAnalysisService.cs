using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    // Maps C# type full names to CustomApiFieldType values.
    private static readonly Dictionary<string, int> FieldTypeMap = new()
    {
        ["System.Boolean"]                          = 0,
        ["System.DateTime"]                         = 1,
        ["System.Decimal"]                          = 2,
        ["Microsoft.Xrm.Sdk.Entity"]                = 3,
        ["Microsoft.Xrm.Sdk.EntityCollection"]      = 4,
        ["Microsoft.Xrm.Sdk.EntityReference"]       = 5,
        ["System.Single"]                           = 6,
        ["System.Double"]                           = 6,
        ["System.Int32"]                            = 7,
        ["Microsoft.Xrm.Sdk.Money"]                 = 8,
        ["Microsoft.Xrm.Sdk.OptionSetValue"]        = 9,
        ["System.String"]                           = 10,
        ["System.String[]"]                         = 11,
        ["System.Guid"]                             = 12,
    };

    public PluginAssemblyMetadata Analyze(string dllPath)
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var paths = Directory.GetFiles(runtimeDir, "*.dll").ToList();

        var assemblyDir = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrWhiteSpace(assemblyDir) && Directory.Exists(assemblyDir))
        {
            foreach (var file in Directory.EnumerateFiles(assemblyDir, "*.dll", SearchOption.AllDirectories))
                paths.Add(file);

            var parentDir = Directory.GetParent(assemblyDir)?.FullName;
            if (parentDir != null && Directory.Exists(parentDir))
            {
                foreach (var file in Directory.EnumerateFiles(parentDir, "*.dll", SearchOption.TopDirectoryOnly))
                    paths.Add(file);
            }

            // Look for a 'lib' or 'ref' folder in the assembly directory or its parent
            // This is useful for providing .NET Framework reference assemblies on Linux.
            string[] libFolderNames = ["lib", "ref", "external"];
            var searchDirs = new List<string> { assemblyDir };
            if (parentDir != null) searchDirs.Add(parentDir);

            foreach (var searchDir in searchDirs)
            {
                foreach (var libName in libFolderNames)
                {
                    var libPath = Path.Combine(searchDir, libName);
                    if (Directory.Exists(libPath))
                    {
                        foreach (var file in Directory.EnumerateFiles(libPath, "*.dll", SearchOption.AllDirectories))
                            paths.Add(file);
                    }
                }
            }
        }

        // Include GAC (Global Assembly Cache) for .NET Framework assemblies like System.Activities
        // This only works on Windows where .NET Framework is installed.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var windir = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(windir))
            {
                var gacPath = Path.Combine(windir, "Microsoft.NET", "assembly");
                if (Directory.Exists(gacPath))
                {
                    // We don't want to load every single DLL in the GAC as it's huge.
                    // But we can add common dependencies if they are not already in paths.
                    string[] commonGacAssemblies = ["System.Activities.dll", "System.Runtime.Serialization.dll", "System.ServiceModel.dll"];

                    foreach (var gacAssemblyName in commonGacAssemblies)
                    {
                        if (paths.All(p => !p.Contains(gacAssemblyName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var foundPath = Directory.EnumerateFiles(gacPath, gacAssemblyName, SearchOption.AllDirectories).FirstOrDefault();
                            if (foundPath != null)
                                paths.Add(foundPath);
                        }
                    }
                }
            }
        }

        paths.Add(dllPath);
        var resolver = new PathAssemblyResolver(paths.Distinct());
        using var mlc = new MetadataLoadContext(resolver);

        var assembly = mlc.LoadFromAssemblyPath(dllPath);
        var assemblyName = assembly.GetName();
        var content = File.ReadAllBytes(dllPath);
        var hash = Convert.ToHexString(SHA256.HashData(content));

        var pktBytes = assemblyName.GetPublicKeyToken();
        var publicKeyToken = pktBytes is { Length: > 0 }
            ? Convert.ToHexString(pktBytes).ToLowerInvariant()
            : null;
        var culture = string.IsNullOrEmpty(assemblyName.CultureName) ? "neutral" : assemblyName.CultureName;

        output.Info($"Loaded assembly {assemblyName.Name}");

        var pluginTypes = new List<PluginTypeMetadata>();
        var potentialPluginTypes = assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true });
        foreach (var type in potentialPluginTypes)
        {
            var isPlugin = IsDerivedFrom(type, "Microsoft.Xrm.Sdk.IPlugin");
            var isWorkflow = IsDerivedFrom(type, "System.Activities.CodeActivity");

            if (!isPlugin && !isWorkflow) continue;

            if (isWorkflow)
            {
                output.Verbose($"Found Workflow {type.FullName}");
                pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, Steps: [], CustomApis: null, IsWorkflow: true));
                continue;
            }

            if (isPlugin)
            {
                var customApi = TryBuildCustomApi(type);
                if (customApi != null)
                {
                    output.Verbose($"Found Custom API {type.FullName}");
                    pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, Steps: [], CustomApis: [customApi], IsWorkflow: false, IsCustomApi: true));
                    continue;
                }

                var step = TryBuildStep(type);
                if (step != null)
                {
                    output.Verbose($"Found Plugin {type.FullName} with plugin step {step.Name}");
                    foreach (var warning in step.Warnings) output.Warning(warning);
                    pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, [step], null, isWorkflow));
                }
                else
                {
                    output.Verbose($"Found Plugin {type.FullName} with no parseable step");
                    pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, [], null, isWorkflow));
                }
            }
        }

        return new PluginAssemblyMetadata(
            assemblyName.Name!,
            assemblyName.FullName,
            content,
            hash,
            assemblyName.Version!.ToString(),
            publicKeyToken,
            culture,
            pluginTypes);
    }

    private CustomApiMetadata? TryBuildCustomApi(Type type)
    {
        var customApiAttr = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Flowline.Attributes.CustomApiAttribute");
        if (customApiAttr == null) return null;

        // Read [CustomApi] attribute values
        string? boundEntity = customApiAttr.ConstructorArguments.Count > 0
            ? customApiAttr.ConstructorArguments[0].Value as string
            : null;

        string? entityCollection = null;
        bool isFunction = false;
        bool isPrivate = false;
        int allowedStepType = 0;
        string? displayName = null;
        string? description = null;
        string? executePrivilege = null;

        foreach (var arg in customApiAttr.NamedArguments)
        {
            switch (arg.MemberName)
            {
                case "EntityCollection": entityCollection = arg.TypedValue.Value as string; break;
                case "IsFunction":       isFunction = (bool)arg.TypedValue.Value!; break;
                case "IsPrivate":        isPrivate = (bool)arg.TypedValue.Value!; break;
                case "AllowedStepType":  allowedStepType = Convert.ToInt32(arg.TypedValue.Value); break;
                case "DisplayName":      displayName = arg.TypedValue.Value as string; break;
                case "Description":      description = arg.TypedValue.Value as string; break;
                case "ExecutePrivilege": executePrivilege = arg.TypedValue.Value as string; break;
            }
        }

        int bindingType;
        string? boundEntityLogicalName;
        if (boundEntity != null)        { bindingType = 1; boundEntityLogicalName = boundEntity; }
        else if (entityCollection != null) { bindingType = 2; boundEntityLogicalName = entityCollection; }
        else                            { bindingType = 0; boundEntityLogicalName = null; }

        var baseName = StripCustomApiSuffix(type.Name);
        displayName ??= SplitPascalCase(baseName);
        description ??= displayName;

        var (requestParams, responseProps) = ReadClassLevelParameters(type, baseName);

        return new CustomApiMetadata(
            baseName, displayName, description,
            bindingType, boundEntityLogicalName,
            isFunction, isPrivate, allowedStepType, executePrivilege,
            type.FullName!,
            requestParams, responseProps);
    }

    private static (List<RequestParameterMetadata>, List<ResponsePropertyMetadata>)
        ReadClassLevelParameters(Type type, string apiBaseName)
    {
        var requestParams = new List<RequestParameterMetadata>();
        var responseProps = new List<ResponsePropertyMetadata>();

        foreach (var attr in type.GetCustomAttributesData())
        {
            if (attr.AttributeType.FullName == "Flowline.Attributes.InputAttribute" &&
                attr.ConstructorArguments.Count == 2)
            {
                var uniqueName = (string)attr.ConstructorArguments[0].Value!;
                var fieldType = Convert.ToInt32(attr.ConstructorArguments[1].Value);
                bool isOptional = false;
                string? entityName = null, displayName = null, description = null;

                foreach (var arg in attr.NamedArguments)
                {
                    switch (arg.MemberName)
                    {
                        case "IsOptional":   isOptional = (bool)arg.TypedValue.Value!; break;
                        case "Entity":   entityName = arg.TypedValue.Value as string; break;
                        case "DisplayName":  displayName = arg.TypedValue.Value as string; break;
                        case "Description":  description = arg.TypedValue.Value as string; break;
                    }
                }

                displayName ??= SplitPascalCase(uniqueName);
                var name = $"{apiBaseName}.{uniqueName}";
                description ??= $"{displayName} ({name})";

                requestParams.Add(new(uniqueName, displayName, name, description, fieldType, isOptional, entityName));
            }
            else if (attr.AttributeType.FullName == "Flowline.Attributes.OutputAttribute" &&
                     attr.ConstructorArguments.Count == 2)
            {
                var uniqueName = (string)attr.ConstructorArguments[0].Value!;
                var fieldType = Convert.ToInt32(attr.ConstructorArguments[1].Value);
                string? entityName = null, displayName = null, description = null;

                foreach (var arg in attr.NamedArguments)
                {
                    switch (arg.MemberName)
                    {
                        case "Entity":   entityName = arg.TypedValue.Value as string; break;
                        case "DisplayName":  displayName = arg.TypedValue.Value as string; break;
                        case "Description":  description = arg.TypedValue.Value as string; break;
                    }
                }

                displayName ??= SplitPascalCase(uniqueName);
                var name = $"{apiBaseName}.{uniqueName}";
                description ??= $"{displayName} ({name})";

                responseProps.Add(new(uniqueName, displayName, name, description, fieldType, entityName));
            }
        }

        return (requestParams, responseProps);
    }

    private static PluginStepMetadata? TryBuildStep(Type type)
    {
        var stepAttr = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Flowline.Attributes.StepAttribute");
        if (stepAttr == null) return null;

        var entity = stepAttr.ConstructorArguments.Count > 0
            ? (string?)stepAttr.ConstructorArguments[0].Value
            : null;
        var order = 1;
        var configuration = (string?)null;
        var runAsString = (string?)null;
        bool? deleteJobOnSuccessExplicit = null;
        foreach (var arg in stepAttr.NamedArguments)
        {
            if (arg.MemberName == "Order") order = Convert.ToInt32(arg.TypedValue.Value);
            else if (arg.MemberName == "Configuration") configuration = (string?)arg.TypedValue.Value;
            else if (arg.MemberName == "RunAs") runAsString = (string?)arg.TypedValue.Value;
            else if (arg.MemberName == "DeleteJobOnSuccess") deleteJobOnSuccessExplicit = (bool)arg.TypedValue.Value!;
        }
        var deleteJobOnSuccess = deleteJobOnSuccessExplicit ?? true;

        ValidateLogicalName(type.Name, entity);

        if (!TryParseClassName(type.Name, out var message, out var stage, out var mode))
            return null;

        ValidateExecutionMode(type.Name, stage, mode);
        ValidateCustomApiAttributesOnStep(type.Name, HasCustomApiAttributes(type));

        var filteringAttributes = ReadFilterAttributes(type);
        ValidateFilter(type.Name, message, filteringAttributes);

        var (hasSecondaryAttr, secondaryEntity) = ReadSecondaryEntityAttribute(type);
        ValidateSecondaryLogicalName(type.Name, secondaryEntity);
        ValidateSecondaryEntity(type.Name, message, hasSecondaryAttr, secondaryEntity);

        var images = ReadImageAttributes(type);
        ValidateImages(type.Name, message, stage, images);
        Guid? runAs = runAsString != null && Guid.TryParse(runAsString, out var parsed) ? parsed : (Guid?)null;

        var warnings = BuildStepWarnings(type.Name, message, entity, filteringAttributes, hasSecondaryAttr, secondaryEntity, images);
        if (deleteJobOnSuccessExplicit == true && mode != (int)ProcessingMode.Asynchronous)
            warnings.Add($"DeleteJobOnSuccess = true has no effect on synchronous step '{type.Name}'.");
        if (runAsString != null && runAs == null)
            warnings.Add($"RunAs value '{runAsString}' on '{type.Name}' is not a valid GUID — impersonatinguserid will not be set.");

        var entityDisplay = entity ?? "any";
        var stepName = secondaryEntity != null
            ? $"{type.FullName}: {message} of {entityDisplay} with {secondaryEntity}"
            : $"{type.FullName}: {message} of {entityDisplay}";

        return new PluginStepMetadata(stepName, message, entity, stage, mode, order, filteringAttributes, configuration, images, warnings, secondaryEntity, deleteJobOnSuccess, runAs);
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
        var normalized = attrs.Select(v => v.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();

        return normalized.Length > 0 ? string.Join(",", normalized) : null;
    }

    internal static void ValidateLogicalName(string className, string? entity)
    {
        if (entity is not null && string.IsNullOrWhiteSpace(entity))
            throw new InvalidOperationException(
                $"{className}: [Step] entity cannot be an empty string — use [Step(\"none\")] to register on all tables, " +
                $"or specify a table logical name e.g. [Step(\"account\")].");
    }

    internal static void ValidateSecondaryLogicalName(string className, string? entity)
    {
        if (entity is not null && string.IsNullOrWhiteSpace(entity))
            throw new InvalidOperationException(
                $"{className}: [SecondaryEntity] entity cannot be an empty string — use [SecondaryEntity(\"none\")] to match any secondary table, " +
                $"or specify a table logical name e.g. [SecondaryEntity(\"account\")].");
    }

    internal static void ValidateCustomApiAttributesOnStep(string className, bool hasCustomApiAttributes)
    {
        if (!hasCustomApiAttributes) return;

        throw new InvalidOperationException(
            $"{className}: [Input] and [Output] are Custom API attributes — they have no effect on a plugin step. " +
            $"Add [CustomApi] to register this class as a Custom API, or remove [Input]/[Output].");
    }

    private static bool HasCustomApiAttributes(Type type) =>
        type.GetCustomAttributesData().Any(a => a.AttributeType.FullName is
            "Flowline.Attributes.InputAttribute" or "Flowline.Attributes.OutputAttribute");

    private static (bool hasAttribute, string? entity) ReadSecondaryEntityAttribute(Type type)
    {
        var attr = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Flowline.Attributes.SecondaryEntityAttribute");
        if (attr == null) return (false, null);
        var entity = attr.ConstructorArguments.Count > 0
            ? (string?)attr.ConstructorArguments[0].Value
            : null;
        return (true, entity);
    }

    internal static void ValidateSecondaryEntity(string className, string message, bool hasSecondaryAttr, string? secondaryEntity)
    {
        if (hasSecondaryAttr && secondaryEntity is not (null or "none") && message is not ("Associate" or "Disassociate"))
            throw new InvalidOperationException(
                $"{className}: [SecondaryEntity] has no effect on {message} — it only applies to Associate and Disassociate steps. " +
                $"Remove [SecondaryEntity] or change the step message.");
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

    internal static void ValidateFilter(string className, string message, string? filteringAttributes)
    {
        if (filteringAttributes == null || message == "Update") return;

        throw new InvalidOperationException(
            $"{className}: [Filter] has no effect on {message} — column filtering only applies to Update steps. " +
            $"Remove [Filter] or change the step to Update.");
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

    private static List<string> BuildStepWarnings(string className, string message, string? entity, string? filteringAttributes, bool hasSecondaryAttr, string? secondaryEntity, List<PluginImageMetadata> images)
    {
        var warnings = new List<string>();

        if (entity == null)
            warnings.Add(
                $"{className}: [Step] has no entity — this step will fire for all tables. " +
                $"If intentional, specify [Step(\"none\")] to suppress this warning.");

        if (message == "Update" && filteringAttributes == null)
            warnings.Add(
                $"{className}: Update step has no [Filter] — it will fire on every update to the table, regardless of which columns changed. " +
                $"Add [Filter] with the columns your plugin cares about. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#set-filtering-attributes");

        if (message is "Associate" or "Disassociate")
        {
            if (!hasSecondaryAttr)
                warnings.Add(
                    $"{className}: {message} step has no [SecondaryEntity] — it will fire for any table on the other side of the relationship. " +
                    $"Add [SecondaryEntity(\"none\")] to make this explicit, or specify the exact secondary table logical name.");
            else if (secondaryEntity == null)
                warnings.Add(
                    $"{className}: [SecondaryEntity] has no entity — this step will fire for all secondary tables. " +
                    $"If intentional, specify [SecondaryEntity(\"none\")] to suppress this warning.");
        }

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

    // Strips Api, CustomApi, or Plugin suffix to get the base name for uniqueName derivation.
    internal static string StripCustomApiSuffix(string className)
    {
        if (className.EndsWith("CustomApi", StringComparison.Ordinal)) return className[..^9];
        if (className.EndsWith("Api", StringComparison.Ordinal))       return className[..^3];
        if (className.EndsWith("Plugin", StringComparison.Ordinal))    return className[..^6];
        return className;
    }

    // Splits PascalCase into "Title Case" words, e.g. "GetAccountRisk" → "Get Account Risk".
    internal static string SplitPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                result.Append(' ');
            result.Append(name[i]);
        }
        return result.ToString();
    }

}
