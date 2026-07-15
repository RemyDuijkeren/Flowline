using System.Reflection;
using Flowline.Attributes;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

// Code-review split (maintainability P1): extracted from PluginAssemblyReader, which crossed 1000 lines.
// Owns reflection-based attribute/step/CustomApi metadata parsing for one already-loaded assembly;
// PluginAssemblyReader keeps only assembly loading/resolution (Analyze, AnalyzePackage, BuildResolverPaths,
// ExtractLibDlls, BuildAssemblyMetadata) and delegates scanning to this class.
public class PluginTypeMetadataScanner(IAnsiConsole console)
{
    private static readonly string[] MessageNames =
        Enum.GetNames<Message>().OrderByDescending(n => n.Length).ToArray();

    // Maps Message enum int values to their name strings. Built at startup from the live runtime
    // type (not MetadataLoadContext) so Enum.GetValues<Message>() is safe to call here.
    private static readonly Dictionary<int, string> MessageValueToName =
        Enum.GetValues<Message>().ToDictionary(v => (int)v, v => v.ToString());

    // Messages that support entity images.
    // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#define-entity-images
    private static readonly HashSet<string> ImageSupportedMessages =
    [
        "Assign", "Create", "CreateMultiple", "Delete", "DeliverIncoming", "DeliverPromote",
        "Merge", "Route", "Send", "SetState", "Update", "UpdateMultiple"
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

    public List<PluginTypeMetadata> ScanPluginTypes(Assembly assembly)
    {
        var pluginTypes = new List<PluginTypeMetadata>();
        var candidates = assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true });
        foreach (var type in candidates)
        {
            var isPlugin = IsDerivedFrom(type, "Microsoft.Xrm.Sdk.IPlugin");
            var isWorkflow = IsDerivedFrom(type, "System.Activities.CodeActivity");

            ValidateStepUsage(type.Name, HasStepAttribute(type), isPlugin);

            if (!isPlugin && !isWorkflow) continue;

            if (isWorkflow)
            {
                console.Verbose($"Found Workflow {type.FullName}");
                pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, Steps: [], CustomApis: [], IsWorkflow: true));
                continue;
            }

            // isPlugin guaranteed true here: !isWorkflow and passed the (!isPlugin && !isWorkflow) guard above
            var customApi = TryBuildCustomApi(type);
            if (customApi != null)
            {
                console.Verbose($"Found Custom API {type.FullName}");
                pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, Steps: [], CustomApis: [customApi], IsWorkflow: false, IsCustomApi: true));
                continue;
            }

            var steps = TryBuildSteps(type).ToList();
            if (steps.Count > 0)
            {
                foreach (var s in steps)
                {
                    console.Verbose($"Found Plugin {type.FullName} with plugin step {s.Name}");
                    foreach (var warning in s.Warnings) console.Warning(warning);
                }
                pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, steps, [], isWorkflow));
            }
            else
            {
                console.Verbose($"Found Plugin {type.FullName} with no [[Step]] or [[Custom API]]");
                pluginTypes.Add(new PluginTypeMetadata(type.Name, type.FullName!, [], [], isWorkflow));
            }
        }
        return pluginTypes;
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
        string? uniqueNameOverride = null;

        foreach (var arg in customApiAttr.NamedArguments)
        {
            switch (arg.MemberName)
            {
                case "TableCollection": entityCollection = arg.TypedValue.Value as string; break;
                case "IsFunction":       isFunction = (bool)arg.TypedValue.Value!; break;
                case "IsPrivate":        isPrivate = (bool)arg.TypedValue.Value!; break;
                case "AllowedStepType":  allowedStepType = Convert.ToInt32(arg.TypedValue.Value); break;
                case "DisplayName":      displayName = arg.TypedValue.Value as string; break;
                case "Description":      description = arg.TypedValue.Value as string; break;
                case "ExecutePrivilege": executePrivilege = arg.TypedValue.Value as string; break;
                case "UniqueName":       uniqueNameOverride = arg.TypedValue.Value as string; break;
            }
        }

        ValidateCustomApiUniqueNameFormat(type.Name, uniqueNameOverride);

        int bindingType;
        string? boundEntityLogicalName;
        if (boundEntity != null)        { bindingType = 1; boundEntityLogicalName = boundEntity; }
        else if (entityCollection != null) { bindingType = 2; boundEntityLogicalName = entityCollection; }
        else                            { bindingType = 0; boundEntityLogicalName = null; }

        var baseName = StripCustomApiSuffix(type.Name);

        if (uniqueNameOverride != null)
            displayName ??= SplitPascalCase(uniqueNameOverride[(uniqueNameOverride.IndexOf('_') + 1)..]);
        else
            displayName ??= SplitPascalCase(baseName);
        description ??= displayName;

        var (requestParams, responseProps) = ReadClassLevelParameters(type, baseName);

        return new CustomApiMetadata(
            baseName, displayName, description,
            bindingType, boundEntityLogicalName,
            isFunction, isPrivate, allowedStepType, executePrivilege,
            type.FullName!,
            requestParams, responseProps,
            uniqueNameOverride);
    }

    // Format-only check: no live publisher prefix is available yet at read time (PluginPlanner
    // resolves it later and validates the prefix itself matches — see PlanCustomApi).
    internal static void ValidateCustomApiUniqueNameFormat(string className, string? uniqueName)
    {
        if (uniqueName is null) return;

        if (string.IsNullOrWhiteSpace(uniqueName))
            throw new InvalidOperationException(
                $"{className}: [CustomApi] UniqueName cannot be an empty string — omit UniqueName to derive it from the class name, " +
                $"or specify the complete unique name including the publisher prefix, e.g. [CustomApi(UniqueName = \"dev1_MyCustomApi\")].");

        var underscoreIndex = uniqueName.IndexOf('_');
        if (underscoreIndex < 0)
            throw new InvalidOperationException(
                $"{className}: [CustomApi] UniqueName '{uniqueName}' must be the complete Dataverse unique name including the " +
                $"publisher prefix, e.g. \"dev1_MyCustomApi\" — not just the base name.");

        if (underscoreIndex == uniqueName.Length - 1)
            throw new InvalidOperationException(
                $"{className}: [CustomApi] UniqueName '{uniqueName}' has nothing after the publisher prefix — " +
                $"specify the full name, e.g. \"dev1_MyCustomApi\".");

        if (!char.IsLetter(uniqueName[0]) || uniqueName.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new InvalidOperationException(
                $"{className}: [CustomApi] UniqueName '{uniqueName}' is invalid — it may contain only letters, digits, and underscores, " +
                $"and must start with a letter.");
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
                        case "Table":        entityName = arg.TypedValue.Value as string; break;
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
                        case "Table":        entityName = arg.TypedValue.Value as string; break;
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

    internal static IEnumerable<PluginStepMetadata> TryBuildSteps(Type type)
    {
        var stepAttr = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Flowline.Attributes.StepAttribute");
        if (stepAttr == null) return [];

        var table = stepAttr.ConstructorArguments.Count > 0
            ? (string?)stepAttr.ConstructorArguments[0].Value
            : null;
        var order = 1;
        var configuration = (string?)null;
        var description = (string?)null;
        var runAsString = (string?)null;
        bool? deleteJobOnSuccessExplicit = null;
        var secondaryTable = (string?)null;
        foreach (var arg in stepAttr.NamedArguments)
        {
            if (arg.MemberName == "Order") order = Convert.ToInt32(arg.TypedValue.Value);
            else if (arg.MemberName == "Config") configuration = (string?)arg.TypedValue.Value;
            else if (arg.MemberName == "Description") description = (string?)arg.TypedValue.Value;
            else if (arg.MemberName == "RunAs") runAsString = (string?)arg.TypedValue.Value;
            else if (arg.MemberName == "DeleteJobOnSuccess") deleteJobOnSuccessExplicit = (bool)arg.TypedValue.Value!;
            else if (arg.MemberName == "SecondaryTable") secondaryTable = (string?)arg.TypedValue.Value;
        }

        ValidateLogicalName(type.Name, table);
        ValidateSecondaryLogicalName(type.Name, secondaryTable);
        ValidateCustomApiAttributesOnStep(type.Name, HasCustomApiAttributes(type));

        Guid? runAs = runAsString != null && Guid.TryParse(runAsString, out var parsed) ? parsed : (Guid?)null;

        var allHandlesAttrs = type.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Flowline.Attributes.HandlesAttribute")
            .ToList();

        if (allHandlesAttrs.Count >= 2)
            return BuildMultiHandlesSteps(type, allHandlesAttrs, table, order, configuration, description, runAs, runAsString, deleteJobOnSuccessExplicit, secondaryTable);

        // Single-[Handles] or convention path
        string message;
        int stage;
        int mode;
        var handlesUsed = false;

        var handlesAttr = allHandlesAttrs.Count == 1 ? allHandlesAttrs[0] : null;
        if (handlesAttr != null)
        {
            handlesUsed = true;
            (message, stage, mode) = ParseHandlesAttr(handlesAttr, type.Name);
        }
        else
        {
            (message, stage, mode) = ParseStepClassNameOrThrow(type.Name);
        }

        ValidateExecutionMode(type.Name, stage, mode);

        var filteringColumns = ReadFilterAttributes(type);
        ValidateFilter(type.Name, message, filteringColumns);

        ValidateSecondaryTable(type.Name, message, secondaryTable);

        var images = ReadImageAttributes(type);
        ValidateImages(type.Name, message, stage, images);

        var warnings = BuildStepWarnings(type.Name, message, filteringColumns, images);

        // [Handles] is redundant when the class name parses to the same message+stage+mode.
        if (handlesUsed &&
            TryParseClassName(type.Name, out var parsedMsg, out var parsedStage, out var parsedMode) &&
            parsedMsg == message && parsedStage == stage && parsedMode == mode)
            warnings.Add(
                $"{type.Name}: [[Handles]] is redundant — the class name already encodes the same step registration. Remove [[Handles]] and rely on the naming convention.");

        var deleteJobOnSuccess = (mode == (int)ProcessingMode.Asynchronous) && (deleteJobOnSuccessExplicit ?? true);
        if (deleteJobOnSuccessExplicit == true && mode != (int)ProcessingMode.Asynchronous)
            warnings.Add($"DeleteJobOnSuccess = true has no effect on synchronous step '{type.Name}'.");
        if (runAsString != null && runAs == null)
            warnings.Add($"RunAs value '{runAsString}' on '{type.Name}' is not a valid GUID — impersonatinguserid will not be set.");

        var tableDisplay = table ?? "any";
        var stepName = secondaryTable != null
            ? $"{type.FullName}: {message} of {tableDisplay} with {secondaryTable}"
            : $"{type.FullName}: {message} of {tableDisplay}";

        return [new PluginStepMetadata(stepName, message, table, stage, mode, order, filteringColumns, configuration, images, warnings, secondaryTable, deleteJobOnSuccess, runAs, description)];
    }

    private static IEnumerable<PluginStepMetadata> BuildMultiHandlesSteps(
        Type type,
        List<CustomAttributeData> allHandlesAttrs,
        string? table, int order, string? configuration, string? description, Guid? runAs, string? runAsString,
        bool? deleteJobOnSuccessExplicit, string? secondaryTable)
    {
        var handles = new List<(string Message, int Stage, int Mode, string StageSuffix)>();
        foreach (var hAttr in allHandlesAttrs)
        {
            var (msg, s, m) = ParseHandlesAttr(hAttr, type.Name);
            ValidateSecondaryTable(type.Name, msg, secondaryTable);
            ValidateExecutionMode(type.Name, s, m);
            // Mode captured in suffix so PostOperation sync vs async produce distinct names
            var stageSuffix = m == (int)ProcessingMode.Asynchronous
                ? "PostOperationAsync"
                : ((ProcessingStage)s).ToString();
            handles.Add((msg, s, m, stageSuffix));
        }

        var qualifyWithStage = handles.GroupBy(h => h.Message).Any(g => g.Count() > 1);
        var filteringColumns = ReadFilterAttributes(type);
        var allImages = ReadImageAttributes(type);
        var hasFilter = filteringColumns != null;
        var hasPreImage = allImages.Any(i => i.ImageType == (int)ImageType.PreImage);
        var hasPostImage = allImages.Any(i => i.ImageType == (int)ImageType.PostImage);

        var tableDisplay = table ?? "any";
        var nudgeWarning = $"{type.Name}: multiple [[Handles]] detected — prefer splitting into named subclasses for long-term maintainability.";
        var anyFilterCompatible = false;
        var anyPreImageCompatible = false;
        var anyPostImageCompatible = false;
        var steps = new List<PluginStepMetadata>();
        var nudgeEmitted = false;

        foreach (var (msg, s, m, stageSuffix) in handles)
        {
            var stepFilter = msg is "Update" or "UpdateMultiple" ? filteringColumns : null;
            if (stepFilter != null) anyFilterCompatible = true;

            var preImageOk = msg != "Create" && ImageSupportedMessages.Contains(msg);
            var postImageOk = msg != "Delete" && s == (int)ProcessingStage.PostOperation && ImageSupportedMessages.Contains(msg);

            var stepImages = new List<PluginImageMetadata>();
            if (preImageOk)
            {
                anyPreImageCompatible = true;
                stepImages.AddRange(allImages.Where(i => i.ImageType == (int)ImageType.PreImage));
            }
            if (postImageOk)
            {
                anyPostImageCompatible = true;
                stepImages.AddRange(allImages.Where(i => i.ImageType == (int)ImageType.PostImage));
            }

            var baseName = secondaryTable != null
                ? $"{type.FullName}: {msg} of {tableDisplay} with {secondaryTable}"
                : $"{type.FullName}: {msg} of {tableDisplay}";
            var stepName = qualifyWithStage ? $"{baseName} at {stageSuffix}" : baseName;

            var stepWarnings = BuildStepWarnings(type.Name, msg, stepFilter, stepImages);
            if (!nudgeEmitted) { stepWarnings.Add(nudgeWarning); nudgeEmitted = true; }

            var deleteJobOnSuccess = m == (int)ProcessingMode.Asynchronous && (deleteJobOnSuccessExplicit ?? true);
            if (deleteJobOnSuccessExplicit == true && m != (int)ProcessingMode.Asynchronous)
                stepWarnings.Add($"DeleteJobOnSuccess = true has no effect on synchronous step '{type.Name}'.");
            if (runAsString != null && runAs == null)
                stepWarnings.Add($"RunAs value '{runAsString}' on '{type.Name}' is not a valid GUID — impersonatinguserid will not be set.");

            steps.Add(new PluginStepMetadata(stepName, msg, table, s, m, order, stepFilter, configuration, stepImages, stepWarnings, secondaryTable, deleteJobOnSuccess, runAs, description));
        }

        // R7: class-level checks — error if attribute is present but no step is compatible
        ValidateMultiHandlesFilter(type.Name, hasFilter, anyFilterCompatible);
        ValidateMultiHandlesPreImage(type.Name, hasPreImage, anyPreImageCompatible);
        ValidateMultiHandlesPostImage(type.Name, hasPostImage, anyPostImageCompatible);

        // Uniqueness guard: two [Handles] with the same (message + stage + mode) produce identical names
        var seenNames = new HashSet<string>();
        foreach (var step in steps)
        {
            if (!seenNames.Add(step.Name))
                throw new InvalidOperationException(
                    $"{type.Name}: two or more [[Handles]] attributes produce the same step name '{step.Name}'. " +
                    $"Each registration must be uniquely identifiable — avoid duplicate [[Handles]] attributes.");
        }

        return steps;
    }

    internal static void ValidateStepUsage(string className, bool hasStepAttribute, bool isPlugin)
    {
        if (!hasStepAttribute || isPlugin) return;

        throw new InvalidOperationException(
            $"{className}: [Step] can only be used on classes that implement Microsoft.Xrm.Sdk.IPlugin. " +
            $"Remove [Step] or implement IPlugin.");
    }

    internal static (string message, int stage, int mode) ParseStepClassNameOrThrow(string className)
    {
        if (TryParseClassName(className, out var message, out var stage, out var mode))
            return (message, stage, mode);

        var strippedName = className;
        if (strippedName.EndsWith("Plugin", StringComparison.Ordinal))
            strippedName = strippedName[..^6];
        if (strippedName.EndsWith("Async", StringComparison.Ordinal))
            strippedName = strippedName[..^5];

        var hasMessage = MessageNames.Any(m => strippedName.EndsWith(m, StringComparison.Ordinal));
        var hasStage = EndsWithStageKeyword(strippedName);

        var reason = (hasStage, hasMessage) switch
        {
            (false, false) => "it does not contain a recognizable stage or message",
            (false, true)  => "it contains a recognizable message but no stage",
            (true, false)  => "it contains a recognizable stage but no message",
            _              => "the stage and message are not in the expected order"
        };

        throw new InvalidOperationException(
            $"{className}: [Step] declares that this class should be registered as a plugin step, but {reason}. " +
            $"Expected pattern: {{Name}}{{Stage}}{{Message}}[Async][Plugin]. " +
            $"Examples: AccountPreCreatePlugin, AccountPostUpdatePlugin, AccountPostUpdateAsyncPlugin, AccountValidationDeletePlugin. " +
            $"Valid stages: Validation, Pre, Post. Valid messages come from {nameof(Message)}.");
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

    private static bool EndsWithStageKeyword(string name) =>
        name.EndsWith("Validation", StringComparison.Ordinal) ||
        name.EndsWith("Pre", StringComparison.Ordinal) ||
        name.EndsWith("Post", StringComparison.Ordinal);

    private static bool HasStepAttribute(Type type) =>
        type.GetCustomAttributesData()
            .Any(a => a.AttributeType.FullName == "Flowline.Attributes.StepAttribute");

    private static string? ReadFilterAttributes(Type type)
    {
        var filterAttr = type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Flowline.Attributes.FilterAttribute");
        if (filterAttr == null) return null;

        var attrs = ReadStringArray(filterAttr.ConstructorArguments[0]);
        var normalized = attrs.Select(v => v.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();

        return normalized.Length > 0 ? string.Join(",", normalized) : null;
    }

    internal static void ValidateLogicalName(string className, string? table)
    {
        if (table is null)
            throw new InvalidOperationException(
                $"{className}: [Step] has no table — specify a table logical name e.g. [Step(\"account\")], " +
                $"or use [Step(\"none\")] to explicitly register on all tables.");

        if (string.IsNullOrWhiteSpace(table))
            throw new InvalidOperationException(
                $"{className}: [Step] table cannot be an empty string — use [Step(\"none\")] to register on all tables, " +
                $"or specify a table logical name e.g. [Step(\"account\")].");
    }

    internal static void ValidateSecondaryLogicalName(string className, string? table)
    {
        if (table is not null && string.IsNullOrWhiteSpace(table))
            throw new InvalidOperationException(
                $"{className}: SecondaryTable cannot be an empty string — use SecondaryTable = \"none\" to match any secondary table, " +
                $"or specify a table logical name e.g. [Step(\"contact\", SecondaryTable = \"account\")].");
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

    internal static void ValidateSecondaryTable(string className, string message, string? secondaryTable)
    {
        if (message is "Associate" or "Disassociate")
        {
            if (secondaryTable is null)
                throw new InvalidOperationException(
                    $"{className}: {message} step has no SecondaryTable — specify the secondary table logical name e.g. SecondaryTable = \"contact\", " +
                    $"or use SecondaryTable = \"none\" to explicitly match any secondary table.");
            return;
        }

        if (secondaryTable is not (null or "none"))
            throw new InvalidOperationException(
                $"{className}: SecondaryTable has no effect on {message} — it only applies to Associate and Disassociate steps. " +
                $"Remove SecondaryTable from [Step] or change the step message.");
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

    internal static void ValidateFilter(string className, string message, string? filteringColumns)
    {
        if (filteringColumns == null || message is "Update" or "UpdateMultiple") return;

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

    internal static void ValidateMultiHandlesFilter(string className, bool hasFilter, bool anyFilterCompatible)
    {
        if (!hasFilter || anyFilterCompatible) return;
        throw new InvalidOperationException(
            $"{className}: [Filter] is present but none of the [[Handles]] registrations are Update or UpdateMultiple — filtering columns have no effect. " +
            $"Remove [Filter] or add an Update or UpdateMultiple [[Handles]].");
    }

    internal static void ValidateMultiHandlesPreImage(string className, bool hasPreImage, bool anyPreImageCompatible)
    {
        if (!hasPreImage || anyPreImageCompatible) return;
        throw new InvalidOperationException(
            $"{className}: [PreImage] is present but none of the [[Handles]] registrations support pre-images. " +
            $"Remove [PreImage] or add a non-Create [[Handles]] on a supported message.");
    }

    internal static void ValidateMultiHandlesPostImage(string className, bool hasPostImage, bool anyPostImageCompatible)
    {
        if (!hasPostImage || anyPostImageCompatible) return;
        throw new InvalidOperationException(
            $"{className}: [PostImage] is present but no [[Handles]] registration is compatible — [PostImage] requires a PostOperation step on a non-Delete message. " +
            $"Remove [PostImage] or add a compatible [[Handles]].");
    }

    private static List<string> BuildStepWarnings(string className, string message, string? filteringColumns, List<PluginImageMetadata> images)
    {
        var warnings = new List<string>();

        if (message is "Update" or "UpdateMultiple" && filteringColumns == null)
            warnings.Add(
                $"{className}: Update step has no [[Filter]] — it will fire on every update to the table, regardless of which columns changed. " +
                $"Add [[Filter]] with the columns your plugin cares about. " +
                $"See https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#set-filtering-attributes");

        if (message == "DeleteMultiple")
            warnings.Add(
                $"{className}: DeleteMultiple is only supported on elastic tables. " +
                $"Using it on a standard table will fail at runtime with 'DeleteMultiple has not yet been implemented.' " +
                $"Verify your table is elastic before pushing.");

        foreach (var image in images)
        {
            if (image.Attributes != null) continue;
            var attrName = image.ImageType == (int)ImageType.PreImage ? "PreImage" : "PostImage";
            warnings.Add(
                $"{className}: [[{attrName}]] has no column filter — all columns will be fetched, which negatively impacts performance. " +
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

        string alias = imageType == (int)ImageType.PreImage ? "preimage" : "postimage";
        string name  = imageType == (int)ImageType.PreImage ? "Pre Image" : "Post Image";

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

    private static (string message, int stage, int mode) ParseHandlesAttr(CustomAttributeData attr, string className)
    {
        var onArg = attr.ConstructorArguments[0];
        string message;
        if (onArg.ArgumentType.FullName == "System.String")
        {
            var msg = (string)onArg.Value!;
            if (string.IsNullOrWhiteSpace(msg))
                throw new InvalidOperationException(
                    $"{className}: [Handles] message name cannot be empty or whitespace — specify a valid Dataverse message name.");
            message = msg;
        }
        else
        {
            var enumValue = Convert.ToInt32(onArg.Value);
            if (!MessageValueToName.TryGetValue(enumValue, out var named))
                throw new InvalidOperationException(
                    $"{className}: [Handles] uses an unknown Message enum value ({enumValue}).");
            message = named;
        }

        // Maps a Stage enum int value (0-3) to the internal ProcessingStage/ProcessingMode pair.
        // Throws NotSupportedException for unknown values so new enum members fail loudly.
        var stageValue = Convert.ToInt32(attr.ConstructorArguments[1].Value);
        var (stage, mode) = stageValue switch
        {
            0 => ((int)ProcessingStage.PreValidation,  (int)ProcessingMode.Synchronous),
            1 => ((int)ProcessingStage.PreOperation,   (int)ProcessingMode.Synchronous),
            2 => ((int)ProcessingStage.PostOperation,  (int)ProcessingMode.Synchronous),
            3 => ((int)ProcessingStage.PostOperation,  (int)ProcessingMode.Asynchronous),
            _ => throw new NotSupportedException(
                $"{className}: [Handles] uses an unknown Stage enum value ({stageValue}). " +
                $"Expected 0 (PreValidation), 1 (PreOperation), 2 (PostOperation), or 3 (PostOperationAsync).")
        };

        return (message, stage, mode);
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
