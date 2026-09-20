using Flowline.Utils;
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
    /// <remarks>
    /// Yellow and glyphed as a warning rather than as neutral info: it is a heads-up the reader is
    /// meant to act on if they disagree, which is the one thing that makes an opt-out default
    /// defensible. Colour is applied only when stderr is a terminal, so a CI log does not collect
    /// escape codes.
    /// </remarks>
    public const string Text = """
        ! Flowline CLI collects usage data in order to help us improve your experience. Data is not
          shared and identifying values are hashed first. The local log file shows exactly what was
          sent. To turn this off: https://github.com/RemyDuijkeren/Flowline/wiki/18-Telemetry
        """;

    const string Yellow = "\u001b[33m";
    const string Reset = "\u001b[0m";

    public static void ShowOnce() => ShowOnce(new ValidationCacheStore(), Console.Error, !Console.IsErrorRedirected);

    internal static void ShowOnce(ValidationCacheStore store, TextWriter stderr, bool useColour = false)
    {
        try
        {
            var cache = store.Load();
            // Per version, the way the dotnet CLI keys its own first-use sentinel to the SDK version.
            // An update is the only moment a user is reliably paying attention to what the tool now
            // does, and it is the only moment collection can have widened without them noticing.
            if (cache.TelemetryDisclosureVersion == FlowlineVersion.Display) return;

            // Written first, then recorded as written. The plan had this the other way round, to stop a
            // failure mid-write producing the notice twice; but marking first means a stderr that
            // throws leaves the run consenting on the user's behalf having told them nothing, and
            // every later run reads the marker and stays silent. The plan's own test list already
            // prefers the other trade ("repeating it beats failing a command"), so it applies to both
            // halves here.
            stderr.WriteLine(useColour ? Yellow + Text + Reset : Text);
            stderr.Flush();

            try
            {
                cache.TelemetryDisclosureShownAtUtc = DateTimeOffset.UtcNow;
                cache.TelemetryDisclosureVersion = FlowlineVersion.Display;
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
