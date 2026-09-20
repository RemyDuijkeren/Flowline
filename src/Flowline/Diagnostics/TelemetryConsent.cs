namespace Flowline.Diagnostics;

// Single place that decides whether telemetry runs for this process, before anything is built.
// Pure decision function (IsEnabled(...)) plus one thin default-wiring entry point, so precedence
// stays testable without mutating real process environment variables.
public static class TelemetryConsent
{
    const string FlowlineOptOutVariable = "FLOWLINE_TELEMETRY_OPTOUT";
    const string PacOptOutVariable = "PP_TOOLS_TELEMETRY_OPTOUT";

    public static bool IsEnabled(string? connectionString, Func<string, string?> getEnvironmentVariable, bool? pacTelemetryEnabled)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        if (IsOptOut(getEnvironmentVariable(FlowlineOptOutVariable)) || IsOptOut(getEnvironmentVariable(PacOptOutVariable)))
            return false;

        // pac saying "disabled" (false) turns telemetry off; pac saying "enabled" (true) or no
        // signal (null) never turns it on by itself.
        if (pacTelemetryEnabled == false)
            return false;

        return true;
    }

    public static bool IsEnabled() =>
        IsEnabled(TelemetryConnectionString.Value, Environment.GetEnvironmentVariable, new PacTelemetrySetting().ReadTelemetryEnabled());

    static bool IsOptOut(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            _ => false,
        };
}
