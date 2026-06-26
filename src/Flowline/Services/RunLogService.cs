using System.Text.Json;
using System.Text.Json.Serialization;
using Flowline.Utils;
using Flowline.Validation;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Flowline.Tests")]

namespace Flowline.Services;

sealed class RunLogService
{
    static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task AppendAsync(RunLogRecord record)
    {
        var path = FlowlineStoragePaths.GetRunsPath(DateOnly.FromDateTime(record.Timestamp.UtcDateTime));
        await AppendToAsync(record, path);
    }

    internal async Task AppendToAsync(RunLogRecord record, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(record, s_jsonOptions);
            await File.AppendAllTextAsync(path, json + Environment.NewLine);
        }
        catch { } // Intentional: log failures must not affect command outcome (R16).
    }

    public Task CleanOldLogsAsync(DateOnly today)
    {
        try
        {
            CleanDirectory(FlowlineStoragePaths.GetRunsPath(today), ".jsonl", today);
            CleanDirectory(FlowlineStoragePaths.GetLogsPath(today), ".log", today);
        }
        catch { } // Intentional: log failures must not affect command outcome (R16).
        return Task.CompletedTask;
    }

    internal static void CleanDirectory(string samplePath, string extension, DateOnly today)
    {
        var dir = Path.GetDirectoryName(samplePath);
        if (dir == null || !Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*" + extension))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var fileDate) && today.DayNumber - fileDate.DayNumber > 30)
            {
                try { File.Delete(file); } catch { } // Intentional: log failures must not affect command outcome (R16).
            }
        }
    }

    public Dictionary<string, string?> ReadToolVersions()
    {
        try
        {
            var cache = new ValidationCacheStore().Load();
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "dotnet", "pac", "git" })
                result[key] = cache.ToolChecks.TryGetValue(key, out var entry) ? entry.Value.Version : null;
            return result;
        }
        catch
        {
            return new Dictionary<string, string?>();
        }
    }
}
