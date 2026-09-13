using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flowline.Core.Configure;

/// <summary>The declared state of one component in a settings file.</summary>
/// <remarks>
/// <paramref name="Enabled"/> is the whole declaration (R3): a component the file names is reconciled to
/// this value, and one it does not name is left untouched. There is no third state — a component whose
/// current state Flowline cannot express is reported, never silently coerced.
/// </remarks>
public sealed record ComponentStateEntry(string Name, bool Enabled);

/// <summary>
/// The in-memory shape of a settings file: PAC's own sections carried verbatim, plus Flowline's typed ones.
/// </summary>
/// <remarks>
/// <b>PAC's sections are a pass-through bag, not a typed model.</b> `pac solution create-settings` owns that
/// half of the file and Microsoft grows it without warning — `CopilotAgents` appeared in real files while the
/// published parameter docs still listed two sections. Deserializing it into named properties would silently
/// drop whatever arrives next, so every top-level property Flowline does not own is kept as a raw node and
/// written back untouched (KTD on PAC-generated sections, R12a).
///
/// The three Flowline-owned sections are typed, because Flowline is the thing that reads them. They are
/// name-to-boolean maps rather than arrays of objects: a solution runs to dozens of flows, and four lines per
/// entry buries the handful that carry a deliberate exception. PAC's own sections keep their array shape,
/// because PAC writes them.
/// </remarks>
public sealed class SettingsDocument
{
    /// <summary>Top-level property name holding declared cloud flow state.</summary>
    public const string CloudFlowsProperty = "CloudFlows";

    /// <summary>Top-level property name holding declared classic workflow state.</summary>
    public const string WorkflowsProperty = "Workflows";

    /// <summary>Top-level property name holding declared plugin step state.</summary>
    public const string PluginStepsProperty = "PluginSteps";

    /// <summary>Top-level property name holding declared business rule state.</summary>
    public const string BusinessRulesProperty = "BusinessRules";

    /// <summary>Top-level property name holding declared business process flow state.</summary>
    public const string BusinessProcessFlowsProperty = "BusinessProcessFlows";

    /// <summary>Top-level property name holding declared custom process action state.</summary>
    public const string ActionsProperty = "Actions";

    /// <summary>Top-level property name holding declared main form activation.</summary>
    public const string FormsProperty = "Forms";

    /// <summary>Top-level property name holding declared public view state.</summary>
    public const string ViewsProperty = "Views";

    /// <summary>
    /// The section holding declared state for one class, or <c>null</c> when the class carries a value
    /// rather than a state.
    /// </summary>
    /// <remarks>
    /// The one place that knows which section holds which class. It exists because there used to be two,
    /// and they drifted: the apply path's own copy listed cloud flows, classic workflows and plugin steps
    /// and answered "no section" for everything else. The effect was silent and specific — a run that
    /// switched a business rule, a business process flow, an action, a form or a view never noticed that
    /// the file declared the opposite, so it never offered to fix it and the next push quietly undid the
    /// change.
    ///
    /// Anything that needs to find a class's section goes through here, so a class added to the document
    /// cannot be half-wired again.
    /// </remarks>
    public IList<ComponentStateEntry>? StateSection(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => CloudFlows,
        ConfigurableComponentKind.Workflow => Workflows,
        ConfigurableComponentKind.BusinessRule => BusinessRules,
        ConfigurableComponentKind.BusinessProcessFlow => BusinessProcessFlows,
        ConfigurableComponentKind.Action => Actions,
        ConfigurableComponentKind.PluginStep => PluginSteps,
        ConfigurableComponentKind.Form => Forms,
        ConfigurableComponentKind.View => Views,
        _ => null,
    };

    /// <summary>Every top-level property Flowline does not own, in the order the source file carried them.</summary>
    public IDictionary<string, JsonNode?> PassThrough { get; init; } = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    /// <summary>Declared state for Power Automate cloud flows, keyed by <c>workflow.uniquename</c> (KTD9).</summary>
    public IList<ComponentStateEntry> CloudFlows { get; init; } = [];

    /// <summary>Declared state for business rules, keyed by <c>workflow.uniquename</c>.</summary>
    public IList<ComponentStateEntry> BusinessRules { get; init; } = [];

    /// <summary>
    /// Declared state for business process flows, keyed by <b>display name</b>.
    /// </summary>
    /// <remarks>
    /// The one section not keyed on a name that survives a rename. Dataverse generates a business process
    /// flow's unique name from the backing entity it creates, so it reads as
    /// <c>msdyn_bpf_d3d97bac8c294105840e99e37a9d1c39</c>: unusable in a file people edit by hand, and
    /// unusable at a prompt. Renaming one in the maker portal orphans its entry here, which the capture
    /// reports as vanished rather than dropping.
    /// </remarks>
    public IList<ComponentStateEntry> BusinessProcessFlows { get; init; } = [];

    /// <summary>Declared state for custom process actions, keyed by <c>workflow.uniquename</c>.</summary>
    public IList<ComponentStateEntry> Actions { get; init; } = [];

    /// <summary>Declared state for classic Dataverse workflows, keyed by <c>workflow.uniquename</c> (KTD9).</summary>
    /// <remarks>
    /// Separate from <see cref="CloudFlows"/> because they are separate things to the person editing the
    /// file, even though Dataverse keeps both in the <c>workflow</c> table.
    /// </remarks>
    public IList<ComponentStateEntry> Workflows { get; init; } = [];

    /// <summary>Declared state for plugin steps, keyed by step unique name (KTD9).</summary>
    public IList<ComponentStateEntry> PluginSteps { get; init; } = [];

    /// <summary>
    /// Declared activation for main forms, keyed by <c>table.form name</c>.
    /// </summary>
    /// <remarks>
    /// Qualified by table because a bare form name addresses nothing -- every table has an Information
    /// form. Main forms only: the other form types either refuse the write or accept it and do nothing.
    ///
    /// Unlike every section above it, a capture never fills this one in. A solution holds dozens of forms
    /// and almost none of them are anyone's business to declare, so entries arrive here one at a time,
    /// when a run switches a form and is told to record it.
    /// </remarks>
    public IList<ComponentStateEntry> Forms { get; init; } = [];

    /// <summary>
    /// Declared state for public views, keyed by <c>table.view name</c>.
    /// </summary>
    /// <remarks>
    /// Qualified and capture-exempt for the same reasons as <see cref="Forms"/>. A table's default view
    /// cannot be deactivated, so declaring one <c>false</c> fails the entry rather than the run.
    /// </remarks>
    public IList<ComponentStateEntry> Views { get; init; } = [];

    /// <summary>
    /// The line ending the source file used, inherited on write (R12c).
    /// </summary>
    /// <remarks>
    /// Verified, not assumed: `pac solution create-settings` writes CRLF (and no trailing newline) on
    /// Windows. Pinning either ending would rewrite every line of the file PAC just produced, turning the
    /// first pull into a whole-file diff that buries the one entry that actually changed — the exact churn
    /// R12c exists to prevent. Inherited for the same reason property order is.
    ///
    /// A file Flowline creates from nothing gets LF, so a settings file born on a Windows developer's
    /// machine and one born in Linux CI are byte-identical.
    /// </remarks>
    public string NewLine { get; init; } = "\n";

    /// <summary>
    /// Serialization options for one document's line ending, so a round-trip is byte-stable (R12c).
    /// </summary>
    /// <remarks>
    /// Two-space indented output matches what `pac solution create-settings` writes (verified against a
    /// real solution folder), so a Flowline write does not reformat the whole file and produce a diff that
    /// hides the one line that actually changed.
    /// </remarks>
    internal static JsonSerializerOptions OptionsFor(string newLine) => new()
    {
        WriteIndented = true,
        NewLine = newLine,
        // System.Text.Json defaults to HTML-safe escaping, which turns `<` into `<` and `&` into
        // `&`. This file is committed and hand-edited, never injected into a page: an environment
        // variable holding a URL with a query string would arrive as an unreadable escape sequence, and
        // differ from the bytes PAC and every human writer produce for the same value.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
