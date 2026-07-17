using System.Text.Json;

namespace Flowline.Core.Services.FormEvents.Support;

/// <summary>
/// Persists resolved (entity, form name) -> formId mappings to a JSON file, so a later push can suggest
/// a previously-seen form identity when a name lookup fails (e.g. after a rename). The caller supplies the
/// file path — Flowline.Core has no reference to the Flowline CLI project's FlowlineStoragePaths, so there
/// is no auto-deriving overload here (mirrors TelemetrySaltStore.cs's constructor/path shape).
/// </summary>
public sealed class FormEventIdentityCache(string path)
{
    internal sealed record Entry(string Entity, string Name, Guid FormId, DateTime LastSeenUtc);

    public Guid? TryGet(string entity, string name)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path));
            return entries?.FirstOrDefault(e =>
                string.Equals(e.Entity, entity, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match
                ? match.FormId
                : null;
        }
        catch (Exception)
        {
            // Missing, empty, or corrupt cache file: degrade to a cache-miss rather than failing the push.
            return null;
        }
    }

    public void Set(string entity, string name, Guid formId) => SetMany([(entity, name, formId)]);

    // Batches multiple resolutions into a single read-modify-write instead of one per entry — a caller
    // resolving many forms in one pass (e.g. FormEventReader's snapshot load) would otherwise re-read and
    // re-write the whole growing file once per form.
    public void SetMany(IEnumerable<(string Entity, string Name, Guid FormId)> resolutions)
    {
        try
        {
            var entries = new List<Entry>();
            if (File.Exists(path))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path));
                    if (existing != null) entries.AddRange(existing);
                }
                catch (Exception)
                {
                    // Corrupt existing cache: start fresh rather than failing the write.
                }
            }

            foreach (var (entity, name, formId) in resolutions)
            {
                entries.RemoveAll(e =>
                    string.Equals(e.Entity, entity, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                entries.Add(new Entry(entity, name, formId, DateTime.UtcNow));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch (Exception)
        {
            // A failed cache write shouldn't fail a push that already resolved forms successfully —
            // worst case, the next push just doesn't find these entries and re-resolves by name.
        }
    }
}
