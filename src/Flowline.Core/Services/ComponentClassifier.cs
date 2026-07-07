using System.Xml.Linq;

namespace Flowline.Core.Services;

public enum ComponentAction { AutoDelete, Manual }

/// <summary>
/// S_new candidates parsed from a solution's unpacked source. <see cref="EntityLogicalNames"/> holds
/// entity roots that Solution.xml records by schemaName instead of id. <see cref="NamedComponents"/> holds
/// every other type recorded by schemaName instead of id (e.g. WebResource — its id is not portable
/// across environments, so pac always records it by name) — callers with a live connection must resolve
/// both to live ids and fold them into the orphan-diff "in solution" set themselves.
/// See <see cref="ComponentClassifier.ParseSolutionXmlComponents"/>.
/// </summary>
public sealed record SolutionXmlComponents(
    IReadOnlyList<(Guid ObjectId, int ComponentType)> Components,
    IReadOnlyList<string> EntityLogicalNames,
    IReadOnlyList<(int ComponentType, string SchemaName)> NamedComponents);

/// <summary>
/// CustomApi family names still declared in Package/src/customapis/**. Solution.xml never lists these
/// as RootComponents (componenttype codes are env-specific — see OrphanCleanupService), and the unpacked
/// XML has no GUID at all — uniquename is the only local identity.
/// </summary>
public sealed record CustomApiNames(
    IReadOnlySet<string> ApiUniqueNames,
    IReadOnlySet<string> RequestParameterNames,
    IReadOnlySet<string> ResponsePropertyNames);

public static class ComponentClassifier
{
    // solutioncomponent.componenttype values for AUTO-delete candidates.
    // Low-numbered types (<100) are stable platform constants — same across all Dataverse orgs.
    // Confirmed via PicklistAttributeMetadata on automatevalue-dev (2026-06-24); one org is sufficient.
    // CustomApi family (10034/10036/10037) are environment-specific — they vary per org and
    // must be detected via entity-side queries (customapi/customapirequestparameter/
    // customapiresponseproperty), not by componenttype. See OrphanCleanupService.
    private const int PluginAssembly                = 91;
    private const int PluginType                    = 90;
    private const int SdkMessageProcessingStep      = 92;
    private const int SdkMessageProcessingStepImage = 93;
    private const int WebResource                   = 61;
    private const int Workflow                      = 29;

    // Entity RootComponents in Solution.xml are keyed by schemaName, not id (portable across
    // environments) — ParseSolutionXmlComponents surfaces these separately for live resolution.
    private const int EntityComponentType = 1;

    // componenttype values for subcomponents unpacked under Entities/<name>/** — never listed as
    // top-level RootComponents in Solution.xml, so ScanEntitySubcomponents recovers them by file scan.
    private const int SystemForm = 60;
    private const int SavedQuery = 26;

    // Microsoft system components (out-of-box views, forms, etc.) use this fixed GUID prefix across
    // every Dataverse org. They can surface as solutioncomponent rows but are never user-deletable.
    private const string SystemComponentIdPrefix = "00000000-0000-0000-00aa-";

    static readonly HashSet<int> AutoTypes =
    [
        PluginAssembly,
        PluginType,
        SdkMessageProcessingStep,
        SdkMessageProcessingStepImage,
        WebResource,
        Workflow,
    ];

    public static ComponentAction Classify(int componentType) =>
        AutoTypes.Contains(componentType) ? ComponentAction.AutoDelete : ComponentAction.Manual;

    public static bool IsWellKnownSystemComponent(Guid objectId) =>
        objectId.ToString().StartsWith(SystemComponentIdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses Solution.xml RootComponents to produce S_new candidates for the orphan diff.
    /// Entity roots recorded by schemaName (no id) are returned via <see cref="SolutionXmlComponents.EntityLogicalNames"/>
    /// instead — the caller must resolve them to MetadataIds against a live connection.
    /// Throws <see cref="FileNotFoundException"/> if Solution.xml is absent.
    /// Throws <see cref="InvalidOperationException"/> if XML is malformed.
    /// </summary>
    public static SolutionXmlComponents ParseSolutionXmlComponents(string solutionXmlPath)
    {
        if (!File.Exists(solutionXmlPath))
            throw new FileNotFoundException($"Solution.xml not found at '{solutionXmlPath}' — run 'clone' first.", solutionXmlPath);

        XDocument doc;
        try
        {
            doc = XDocument.Load(solutionXmlPath);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidOperationException($"Solution.xml is malformed at '{solutionXmlPath}': {ex.Message}", ex);
        }

        var rootComponents = doc.Root
            ?.Element("SolutionManifest")
            ?.Element("RootComponents")
            ?.Elements("RootComponent")
            ?? [];

        var components         = new List<(Guid, int)>();
        var entityLogicalNames = new List<string>();
        var namedComponents    = new List<(int, string)>();

        foreach (var component in rootComponents)
        {
            if (!int.TryParse(component.Attribute("type")?.Value, out var type)) continue;

            if (Guid.TryParse(component.Attribute("id")?.Value, out var id))
            {
                components.Add((id, type));
                continue;
            }

            var schemaName = component.Attribute("schemaName")?.Value;
            if (string.IsNullOrEmpty(schemaName)) continue;

            if (type == EntityComponentType)
                entityLogicalNames.Add(schemaName);
            else
                namedComponents.Add((type, schemaName));
        }

        return new SolutionXmlComponents(components.AsReadOnly(), entityLogicalNames.AsReadOnly(), namedComponents.AsReadOnly());
    }

    /// <summary>
    /// Scans unpacked entity folders for subcomponent files that Solution.xml never lists as
    /// top-level RootComponents: Entities/&lt;name&gt;/FormXml/**/{guid}.xml (Form) and
    /// Entities/&lt;name&gt;/SavedQueries/{guid}.xml (View).
    /// </summary>
    public static IReadOnlyList<(Guid ObjectId, int ComponentType)> ScanEntitySubcomponents(string packageSrcRoot)
    {
        var entitiesRoot = Path.Combine(packageSrcRoot, "Entities");
        if (!Directory.Exists(entitiesRoot)) return [];

        var result = new List<(Guid, int)>();

        foreach (var entityDir in Directory.EnumerateDirectories(entitiesRoot))
        {
            CollectGuidFiles(Path.Combine(entityDir, "FormXml"), SystemForm, result);
            CollectGuidFiles(Path.Combine(entityDir, "SavedQueries"), SavedQuery, result);
        }

        return result.AsReadOnly();
    }

    static void CollectGuidFiles(string dir, int componentType, List<(Guid, int)> result)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file).Trim('{', '}');
            if (Guid.TryParse(name, out var id))
                result.Add((id, componentType));
        }
    }

    /// <summary>
    /// Parses committed source under a solution's Package folder into S_new candidates: Solution.xml
    /// RootComponents (<see cref="ParseSolutionXmlComponents"/>) plus entity subcomponents unpacked
    /// under Entities/** (<see cref="ScanEntitySubcomponents"/>). Shared by <c>DeployCommand</c>'s
    /// orphan-cleanup pre-import step and the drift-detection command — both need the identical
    /// committed-source parse, wrapped with the same <see cref="FlowlineException"/> translation so
    /// callers get identical error messages and exit codes.
    /// </summary>
    public static (IReadOnlyList<(Guid ObjectId, int ComponentType)> Components, IReadOnlyList<string> EntityLogicalNames, IReadOnlyList<(int ComponentType, string SchemaName)> NamedComponents) ParseLocalSource(string packageFolder)
    {
        try
        {
            var srcRoot = Path.Combine(packageFolder, "src");
            var parsed = ParseSolutionXmlComponents(Path.Combine(srcRoot, "Other", "Solution.xml"));
            var subcomponents = ScanEntitySubcomponents(srcRoot);
            return (parsed.Components.Concat(subcomponents).ToList(), parsed.EntityLogicalNames, parsed.NamedComponents);
        }
        catch (FileNotFoundException ex)
        {
            throw new FlowlineException(ExitCode.NotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new FlowlineException(ExitCode.ValidationFailed, ex.Message);
        }
    }

    /// <summary>
    /// Reads the attribute LogicalNames still declared in Entities/&lt;entityLogicalName&gt;/Entity.xml
    /// (Entity/EntityInfo/entity/attributes/attribute/LogicalName). Unlike Forms/Views, attribute
    /// subcomponents have no GUID-named file to scan — they're keyed by LogicalName inside Entity.xml,
    /// not by their solutioncomponent MetadataId — so callers must already know the attribute's
    /// LogicalName (via a live metadata lookup) before this can confirm whether it's still in source.
    /// Returns an empty set if the entity folder or Entity.xml is absent.
    /// </summary>
    public static HashSet<string> ScanEntityAttributeLogicalNames(string packageSrcRoot, string entityLogicalName)
    {
        var entitiesRoot = Path.Combine(packageSrcRoot, "Entities");
        if (!Directory.Exists(entitiesRoot)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entityDir = Directory.EnumerateDirectories(entitiesRoot)
            .FirstOrDefault(dir => string.Equals(Path.GetFileName(dir), entityLogicalName, StringComparison.OrdinalIgnoreCase));
        if (entityDir == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entityXmlPath = Path.Combine(entityDir, "Entity.xml");
        if (!File.Exists(entityXmlPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var doc = XDocument.Load(entityXmlPath);
        var logicalNames = doc.Root
            ?.Element("EntityInfo")
            ?.Element("entity")
            ?.Element("attributes")
            ?.Elements("attribute")
            .Select(a => a.Element("LogicalName")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            ?? [];

        return new HashSet<string>(logicalNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans Package/src/customapis/&lt;uniquename&gt;/** for CustomApi family components still in source.
    /// Used to cross-check live CustomApi orphan candidates by name before reporting them — a recreated
    /// CustomApi (same uniquename, new customapiid) is not actually orphaned.
    /// </summary>
    public static CustomApiNames ScanCustomApiNames(string packageSrcRoot)
    {
        const string requestParams  = "customapirequestparameters";
        const string responseProps = "customapiresponseproperties";

        var (apiNames, children) = ScanShapeFolder(packageSrcRoot, "customapis", requestParams, responseProps);

        return new CustomApiNames(apiNames, children[requestParams], children[responseProps]);
    }

    /// <summary>
    /// Scans Package/src/bots/&lt;schemaname&gt;/** for Bot components still in source. Used to cross-check
    /// live Bot orphan candidates by schemaname before reporting them — a Bot re-created with the same
    /// schemaname (new botid) is not actually orphaned. Bot has no request/response-property equivalent,
    /// so unlike ScanCustomApiNames there's only a top-level set, no child collections.
    /// </summary>
    public static HashSet<string> ScanBotSchemaNames(string packageSrcRoot)
    {
        var (top, _) = ScanShapeFolder(packageSrcRoot, "bots");
        return top;
    }

    /// <summary>
    /// Reads the connectionreferencelogicalname attributes still declared in
    /// Other/Customizations.xml's &lt;connectionreferences&gt; section — ConnectionReference has no
    /// dedicated top-level folder (unlike Bot's bots/&lt;schemaname&gt;/bot.xml shape), it's declared
    /// inline in the same Customizations.xml file ParseSolutionXmlComponents reads Solution.xml
    /// alongside. Used to cross-check live ConnectionReference orphan candidates before reporting them.
    /// Returns an empty set if the file or section is missing or empty — never throws.
    /// </summary>
    public static HashSet<string> ScanConnectionReferenceLogicalNames(string packageSrcRoot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var customizationsXmlPath = Path.Combine(packageSrcRoot, "Other", "Customizations.xml");
        if (!File.Exists(customizationsXmlPath)) return result;

        XDocument doc;
        try
        {
            doc = XDocument.Load(customizationsXmlPath);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return result;
        }

        // Descendants(...), not Root.Element(...): unlike Solution.xml's RootComponents (a fixed,
        // known nesting depth), Customizations.xml's <connectionreferences> position under the root
        // isn't verified against a real unpacked solution fixture in this repo — same defensive style
        // as DataverseContextGenerator.ReadConnectionReferences, which reads this same file.
        var logicalNames = doc.Descendants("connectionreferences")
            .Elements("connectionreference")
            .Select(e => e.Attribute("connectionreferencelogicalname")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!);

        foreach (var name in logicalNames)
            result.Add(name);

        return result;
    }

    /// <summary>
    /// Generalized scanner for the schemaname/uniquename-keyed-folder shape shared by CustomApi and Bot:
    /// a top-level folder with one subfolder per component (subfolder name = the component's local key),
    /// plus zero or more named child-collection subfolders one level deeper following the same pattern
    /// (e.g. customapis/&lt;uniquename&gt;/customapirequestparameters/&lt;name&gt;/). Identity comes purely
    /// from folder names — no file inside a subfolder is ever opened, so a subfolder present without its
    /// usual XML file still counts. A missing top-level folder, a missing child-collection folder, or a
    /// component subfolder with no matching child items all resolve to an empty set for that level —
    /// never an exception. Single-collection callers (no <paramref name="childCollectionFolders"/>) get
    /// an empty <c>Children</c> dictionary and just use <c>Top</c>.
    /// </summary>
    internal static (HashSet<string> Top, Dictionary<string, HashSet<string>> Children) ScanShapeFolder(
        string packageSrcRoot, string folderName, params string[] childCollectionFolders)
    {
        var top      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var children = childCollectionFolders.ToDictionary(
            name => name,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var root = Path.Combine(packageSrcRoot, folderName);
        if (!Directory.Exists(root))
            return (top, children);

        foreach (var itemDir in Directory.EnumerateDirectories(root))
        {
            top.Add(Path.GetFileName(itemDir));

            foreach (var childFolder in childCollectionFolders)
            {
                var childDir = Path.Combine(itemDir, childFolder);
                if (!Directory.Exists(childDir)) continue;

                foreach (var dir in Directory.EnumerateDirectories(childDir))
                    children[childFolder].Add(Path.GetFileName(dir));
            }
        }

        return (top, children);
    }
}
