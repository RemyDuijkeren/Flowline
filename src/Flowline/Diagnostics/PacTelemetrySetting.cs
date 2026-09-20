using System.Text.Json;

namespace Flowline.Diagnostics;

// Reads PAC CLI's own opt-out signal, read-only. Never returns true-as-consent: callers only ever
// act on `false` ("pac says telemetry is disabled"). Any other outcome — file absent, unreadable,
// malformed, wrong shape — resolves to null ("no signal"), never a thrown exception.
public sealed class PacTelemetrySetting(string path)
{
    public PacTelemetrySetting() : this(GetDefaultPath())
    {
    }

    // Mirrors the "telemetryEnabled" JSON property, except `true` is folded into null: pac saying
    // telemetry is enabled is not a consent signal Flowline acts on, only pac saying it's off is.
    public bool? ReadTelemetryEnabled()
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("telemetryEnabled", out var property))
                return null;

            if (property.ValueKind == JsonValueKind.False)
                return false;

            // True or any other JSON shape (string, number, object, ...) carries no consent signal.
            return null;
        }
        catch
        {
            return null;
        }
    }

    static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "PowerAppsCli", "usersettings.json");
}
