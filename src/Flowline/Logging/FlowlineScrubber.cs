using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Flowline.Logging;

/// <summary>
/// Owns every rule that turns an identifying value into a salted hash, for the log file and the
/// telemetry alike.
/// </summary>
/// <remarks>
/// One component rather than two implementations held in sync by discipline (KTD2/D9): the log file
/// is meant to be a faithful preview of what Azure receives, which is only true while both sinks
/// run the same rules over the same salt.
///
/// <para><see cref="Current"/> is a process-wide instance because the CLI is one run per process and
/// the alternative is threading a scrubber through every command's constructor. Tests construct
/// instances directly and never touch it.</para>
/// </remarks>
public sealed class FlowlineScrubber(byte[] salt)
{
    static readonly Regex s_url =
        new(@"https?://[^\s""'\]\)\(,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex s_email =
        new(@"([\w.+-]+)@([\w-]+(?:\.[\w-]+)+)", RegexOptions.Compiled);

    // Only the segment that names the user, so the layout around it survives (KTD5): a value reading
    // /home/<hash>/Projects/<hash>/Solution still says what shape the install was, and a wholly
    // hashed path says nothing.
    static readonly Regex s_homeUser =
        new(@"(?<prefix>/home/|/Users/|[A-Za-z]:\\Users\\)(?<user>[^/\\\s""':;,)\]]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Replaced by value rather than by pattern (KTD6): a solution name, a branch name and a project
    // folder name have no distinguishing shape, so a regex would either miss them or hit unrelated
    // text. Copy-on-write because the enrichers read this while the command adds to it mid-run.
    volatile string[] _knownValues = [];

    // Below this, a known value is more likely to collide with ordinary text than to identify anyone
    // — a project folder called "app" would otherwise hash every "app" substring in the log.
    const int MinKnownValueLength = 4;

    public static FlowlineScrubber Current { get; private set; } = new([]);

    public static void Initialize(byte[] salt) => Current = new FlowlineScrubber(salt);

    /// <summary>Registers a value known to identify the user or their client, to be hashed wherever it appears.</summary>
    public void AddKnownValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinKnownValueLength) return;

        var trimmed = value.Trim();
        var current = _knownValues;
        if (current.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;

        _knownValues = [.. current, trimmed];
    }

    public string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        try
        {
            var result = s_url.Replace(value, m => HashUrl(m.Value, salt));
            result = s_email.Replace(result, m => $"usr_{Hash(m.Groups[1].Value, salt)}.tnt_{Hash(m.Groups[2].Value, salt)}");
            result = s_homeUser.Replace(result, m => m.Groups["prefix"].Value + Hash(m.Groups["user"].Value, salt));

            foreach (var known in _knownValues)
                result = result.Replace(known, Hash(known, salt), StringComparison.OrdinalIgnoreCase);

            return result;
        }
        catch
        {
            // A rule that throws must not abort the log write or the export, and must not fall back to
            // emitting the raw value — the whole point of this class is that nothing unscrubbed leaves.
            return "<scrub-failed>";
        }
    }

    /// <summary>
    /// A per-machine dimension derived from the same salt, so it is stable across runs without being
    /// derived from a machine or user name (KTD11). Regenerated with the salt on an ephemeral CI agent,
    /// where it therefore counts jobs rather than machines.
    /// </summary>
    // Longer than the other two: this one has to stay distinct across every machine that reports,
    // rather than across the values inside one run.
    public string MachineId => _machineId ??= HashHex(salt, "flowline.machine"u8.ToArray(), 16);
    string? _machineId;

    // Case-sensitive, unlike Hash: a URL's path can be, and two URLs differing only in case are two
    // URLs.
    internal static string HashUrl(string url, byte[] salt) =>
        HashHex(salt, Encoding.UTF8.GetBytes(url), 8);

    internal static string Hash(string value, byte[] salt) =>
        HashHex(salt, Encoding.UTF8.GetBytes(value.ToLowerInvariant()), 8);

    static string HashHex(byte[] salt, byte[] data, int length) =>
        Convert.ToHexString(HMACSHA256.HashData(salt, data))[..length].ToLowerInvariant();
}
