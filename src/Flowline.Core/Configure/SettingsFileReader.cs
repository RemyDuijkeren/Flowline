using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flowline.Core.Configure;

/// <summary>Parses and writes a settings file, preserving every section Flowline does not own.</summary>
/// <remarks>
/// Pure: no console, no Dataverse, no disk beyond the two file helpers. That is what lets the whole
/// read-modify-write contract be tested without a live environment or a `pac` on the path.
/// </remarks>
public static class SettingsFileReader
{
    /// <summary>Parses settings-file JSON into its typed and pass-through halves.</summary>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ConfigInvalid"/> when the text is not a JSON object, or a Flowline-owned section
    /// is not shaped the way this reader requires (KTD1).
    /// </exception>
    public static SettingsDocument Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"The settings file isn't valid JSON — {ex.Message}", ex);
        }

        if (root is not JsonObject obj)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                "The settings file must contain a JSON object at its root.");

        // Inherited from the source, like property order below. PAC writes CRLF; a file with no line
        // ending of its own (a single-line file, or one Flowline is creating) gets LF.
        var document = new SettingsDocument
        {
            NewLine = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
        };

        foreach (var property in obj.ToList())
        {
            // Detach before storing: a JsonNode keeps a parent pointer, and re-parenting a still-attached
            // node into the output object throws at write time rather than at read time.
            obj.Remove(property.Key);
            var value = property.Value;

            switch (property.Key)
            {
                case SettingsDocument.FlowsProperty:
                    ReadStateEntries(value, property.Key, document.Flows);
                    break;
                case SettingsDocument.PluginStepsProperty:
                    ReadStateEntries(value, property.Key, document.PluginSteps);
                    break;
                default:
                    document.PassThrough[property.Key] = value;
                    break;
            }
        }

        return document;
    }

    /// <summary>Serializes a document back to settings-file JSON.</summary>
    /// <remarks>
    /// Order is inherited from the source, not imposed (R12c). Sorting would reorder every file
    /// `pac solution create-settings` already wrote, turning the first pull into a whole-file diff — the
    /// exact churn R12c exists to prevent. What R12c actually requires is that a second pull against an
    /// unchanged environment produce identical bytes, and preserving order gives that: same file in, same
    /// file out. New entries append rather than sorting themselves into the middle.
    ///
    /// Line endings are inherited for the same reason, and no trailing newline is written — both match what
    /// PAC produces.
    /// </remarks>
    public static string Write(SettingsDocument document)
    {
        var obj = new JsonObject();

        foreach (var pair in document.PassThrough)
            obj[pair.Key] = pair.Value?.DeepClone();

        if (document.Flows.Count > 0)
            obj[SettingsDocument.FlowsProperty] = WriteStateEntries(document.Flows);

        if (document.PluginSteps.Count > 0)
            obj[SettingsDocument.PluginStepsProperty] = WriteStateEntries(document.PluginSteps);

        return obj.ToJsonString(SettingsDocument.OptionsFor(document.NewLine));
    }

    /// <summary>Reads a settings file from disk.</summary>
    /// <exception cref="FlowlineException"><see cref="ExitCode.NotFound"/> when the file is absent.</exception>
    public static SettingsDocument Read(string path)
    {
        if (!File.Exists(path))
            throw new FlowlineException(ExitCode.NotFound,
                $"No settings file at '{path}'. Run 'flowline configure <env> --pull' to create one.");

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Writes a settings file to disk, creating its folder when needed.</summary>
    /// <remarks>
    /// Written to a sibling temp file and moved into place, following
    /// <c>MsBuildSolutionWriter.ReplaceFileAsync</c>. A pull overwrites a file the team has committed and
    /// filled in by hand, so a write that failed halfway would destroy work no re-run can reconstruct.
    /// The move leaves the previous file intact until the replacement is complete on disk.
    /// </remarks>
    public static void Save(SettingsDocument document, string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, Write(document));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    static void ReadStateEntries(JsonNode? node, string section, IList<ComponentStateEntry> into)
    {
        if (node is null)
            return;

        if (node is not JsonArray array)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"The settings file's '{section}' section must be an array.");

        foreach (var element in array)
        {
            if (element is not JsonObject entry)
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"Every entry in '{section}' must be an object with a Name and an Enabled value.");

            var name = entry["Name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"An entry in '{section}' is missing its Name.");

            if (entry["Enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var isEnabled))
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"Entry '{name}' in '{section}' needs an Enabled value of true or false.");

            into.Add(new ComponentStateEntry(name, isEnabled));
        }
    }

    static JsonArray WriteStateEntries(IEnumerable<ComponentStateEntry> entries)
    {
        var array = new JsonArray();

        foreach (var entry in entries)
            array.Add(new JsonObject { ["Name"] = entry.Name, ["Enabled"] = entry.Enabled });

        return array;
    }
}
