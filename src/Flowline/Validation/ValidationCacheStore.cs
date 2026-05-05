using System.Text.Json;

namespace Flowline.Validation;

public sealed class ValidationCacheStore
{
    const int CurrentSchemaVersion = 1;
    static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    readonly string _path;

    public ValidationCacheStore() : this(GetDefaultCachePath())
    {
    }

    public ValidationCacheStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public ValidationCache Load()
    {
        if (!File.Exists(_path))
            return new ValidationCache();

        try
        {
            var json = File.ReadAllText(_path);
            var cache = JsonSerializer.Deserialize<ValidationCache>(json, s_jsonOptions);
            if (cache?.SchemaVersion == CurrentSchemaVersion)
                return cache;
        }
        catch
        {
            // Corrupt cache should never block a command; it will be replaced on next successful check.
        }

        return new ValidationCache();
    }

    public void Save(ValidationCache cache)
    {
        cache.SchemaVersion = CurrentSchemaVersion;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(cache, s_jsonOptions));
    }

    public static string GetDefaultCachePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = string.IsNullOrWhiteSpace(home)
                ? System.IO.Path.GetTempPath()
                : System.IO.Path.Combine(home, ".cache");
        }

        return System.IO.Path.Combine(root, "Flowline", "validation-cache.json");
    }
}
