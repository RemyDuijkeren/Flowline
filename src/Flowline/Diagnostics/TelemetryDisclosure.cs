using Flowline.Validation;

namespace Flowline.Diagnostics;

/// <summary>The one-time notice that telemetry is on, written on the first run that would send.</summary>
/// <remarks>
/// Goes to stderr through <see cref="Console.Error"/> rather than through <c>IAnsiConsole</c> (KTD12):
/// the render hooks tee every Markup write into the log file and filter it by verbosity, and this is
/// neither program output nor a log event. Stderr also keeps it out of piped stdout on CI, which is
/// where a reviewer is most likely to be reading.
/// </remarks>
public static class TelemetryDisclosure
{
    /// <summary>
    /// Bumped by hand when what Flowline collects changes, which re-shows the notice.
    /// </summary>
    /// <remarks>
    /// The dotnet CLI keys its own notice to the SDK version, so an update re-shows it. Keying to the
    /// collection instead of the release says the same thing without nagging: someone who agreed to a
    /// command name and an exit code has not agreed to their log lines, and that is the change worth
    /// interrupting them for. A release that collects nothing new leaves them alone.
    /// </remarks>
    public const int Version = 2;

    public const string Text = """
        · Flowline sends usage and crash telemetry: the command you ran, its exit code, how long each
          step took, your OS and tool versions, and the log lines this run wrote. A failure also sends
          the exception and its stack trace. URLs, email addresses, paths, solution names and branch
          names are hashed first, so your own log file shows exactly what was sent.
          Turn it off with FLOWLINE_TELEMETRY_OPTOUT=1
          More: https://github.com/RemyDuijkeren/Flowline/wiki/18-Telemetry
        """;

    public static void ShowOnce() => ShowOnce(new ValidationCacheStore(), Console.Error);

    internal static void ShowOnce(ValidationCacheStore store, TextWriter stderr)
    {
        try
        {
            var cache = store.Load();
            if (cache.TelemetryDisclosureVersion >= Version) return;

            // Written first, then recorded as written. The plan had this the other way round, to stop a
            // failure mid-write producing the notice twice; but marking first means a stderr that
            // throws leaves the run consenting on the user's behalf having told them nothing, and
            // every later run reads the marker and stays silent. The plan's own test list already
            // prefers the other trade ("repeating it beats failing a command"), so it applies to both
            // halves here.
            stderr.WriteLine(Text);
            stderr.Flush();

            try
            {
                cache.TelemetryDisclosureShownAtUtc = DateTimeOffset.UtcNow;
                cache.TelemetryDisclosureVersion = Version;
                store.Save(cache);
            }
            catch
            {
                // Unwritable cache — the user has been told, and a repeat beats a failure.
            }
        }
        catch
        {
            // Nothing about a disclosure is worth failing a command for (D4).
        }
    }
}
