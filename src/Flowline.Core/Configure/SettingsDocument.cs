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
/// The two Flowline-owned sections are typed, because Flowline is the thing that reads them.
/// </remarks>
public sealed class SettingsDocument
{
    /// <summary>Top-level property name holding declared flow and classic workflow state.</summary>
    public const string FlowsProperty = "Flows";

    /// <summary>Top-level property name holding declared plugin step state.</summary>
    public const string PluginStepsProperty = "PluginSteps";

    /// <summary>Every top-level property Flowline does not own, in the order the source file carried them.</summary>
    public IDictionary<string, JsonNode?> PassThrough { get; init; } = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    /// <summary>Declared state for cloud flows and classic workflows, keyed by <c>workflow.uniquename</c> (KTD9).</summary>
    public IList<ComponentStateEntry> Flows { get; init; } = [];

    /// <summary>Declared state for plugin steps, keyed by step unique name (KTD9).</summary>
    public IList<ComponentStateEntry> PluginSteps { get; init; } = [];

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
    };
}
