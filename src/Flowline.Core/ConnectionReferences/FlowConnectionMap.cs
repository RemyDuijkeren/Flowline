using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flowline.Core.ConnectionReferences;

/// <summary>Reads which connection references a cloud flow's definition names.</summary>
public static class FlowConnectionMap
{
    /// <summary>
    /// The logical names of the connection references a flow's definition names, each once however many
    /// map entries name it, or <c>null</c> when the definition is empty or cannot be read.
    /// </summary>
    /// <remarks>
    /// <c>null</c> is "can't tell", which the classifier treats as "might use anything". An empty set is a
    /// flow that was read and names none. Map keys vary (<c>shared_x</c>, <c>shared_x_1</c>,
    /// <c>shared_x-1</c>), so only the entry values are read.
    /// </remarks>
    public static IReadOnlySet<string>? ReferencedLogicalNames(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition)) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(definition); }
        catch (JsonException) { return null; }

        if (root?["properties"] is not JsonObject properties) return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (properties["connectionReferences"] is not JsonObject map) return names;

        foreach (var entry in map)
        {
            var name = (entry.Value?["connection"]?["connectionReferenceLogicalName"] as JsonValue)?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }

        return names;
    }
}
