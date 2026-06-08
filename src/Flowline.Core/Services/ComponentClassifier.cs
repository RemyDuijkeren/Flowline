using System.Xml.Linq;

namespace Flowline.Core.Services;

public enum ComponentAction { AutoDelete, Manual }

public static class ComponentClassifier
{
    // solutioncomponent.componenttype values for AUTO-delete candidates.
    // Low-numbered types (<100) are stable platform constants across all Dataverse orgs.
    // CustomApi family (10034/10036/10037) are environment-specific — they vary per org and
    // must be detected via entity-side queries (customapi/customapirequestparameter/
    // customapiresponseproperty), not by componenttype. See OrphanCleanupService.
    private const int PluginAssembly                = 91;  // confirmed: PluginService.cs
    private const int PluginType                    = 90;  // TODO: verify against real org
    private const int SdkMessageProcessingStep      = 92;  // confirmed: PluginPlanner.cs
    private const int SdkMessageProcessingStepImage = 93;  // TODO: verify against real org
    private const int WebResource                   = 61;  // confirmed: WebResourceReader.cs
    private const int Workflow                      = 29;  // TODO: verify against real org

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

    /// <summary>
    /// Parses Solution.xml RootComponents to produce S_new for orphan diff.
    /// Throws <see cref="FileNotFoundException"/> if Solution.xml is absent.
    /// Throws <see cref="InvalidOperationException"/> if XML is malformed.
    /// </summary>
    public static IReadOnlyList<(Guid ObjectId, int ComponentType)> ParseSolutionXmlComponents(string solutionXmlPath)
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

        var result = new List<(Guid, int)>();
        foreach (var component in rootComponents)
        {
            if (!int.TryParse(component.Attribute("type")?.Value, out var type)) continue;
            if (!Guid.TryParse(component.Attribute("id")?.Value, out var id)) continue;
            result.Add((id, type));
        }

        return result.AsReadOnly();
    }
}
