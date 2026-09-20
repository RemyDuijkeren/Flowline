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
    // Scrub is handed whole rendered exception chains, so a pathological input is not far-fetched.
    // Without this the email rule could backtrack for a long time on one; with it, that input reaches
    // the catch below and is replaced wholesale instead of hanging a command.
    static readonly TimeSpan s_matchTimeout = TimeSpan.FromMilliseconds(250);

    static readonly Regex s_url =
        new(@"https?://[^\s""'\]\)\(,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, s_matchTimeout);

    static readonly Regex s_email =
        new(@"([\w.+-]+)@([\w-]+(?:\.[\w-]+)+)", RegexOptions.Compiled, s_matchTimeout);

    // A Dataverse host without a scheme, which the URL rule above cannot see. These arrive inside
    // exception text: pac writes them into its stderr, and that stderr is interpolated into the
    // messages this scrubber is handed.
    static readonly Regex s_dataverseHost =
        new(@"\b[\w-]+\.(?:crm[0-9]*|dynamics)\.[\w.-]+\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, s_matchTimeout);

    // Only the segment that names the user, so the layout around it survives (KTD5): a value reading
    // /home/<hash>/Projects/<hash>/Solution still says what shape the install was, and a wholly
    // hashed path says nothing.
    static readonly Regex s_homeUser =
        new(@"(?<prefix>/home/|/Users/|[A-Za-z]:\\Users\\)(?<user>[^/\\\s""':;,)\]]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, s_matchTimeout);

    // Replaced by value rather than by pattern (KTD6): a solution name, a branch name and a project
    // folder name have no distinguishing shape, so a regex would either miss them or hit unrelated
    // text. Copy-on-write so a reader always sees a complete array while the command adds to it
    // mid-run. Single-writer: every registration happens on the main thread before or during command
    // setup. Two concurrent writers would silently lose one registration, so keep it that way.
    volatile string[] _knownValues = [];

    // Below this, a known value is more likely to collide with ordinary text than to identify anyone
    // — a project folder called "app" would otherwise hash every "app" substring in the log.
    const int MinKnownValueLength = 4;

    // Names that identify nobody and appear inside ordinary words. Registering "main" would rewrite
    // every "domain" and "remaining" in the log as "do<hash>" and "re<hash>ing", which corrupts the
    // one artefact a user reads to understand a failure. A word-boundary match is not the answer:
    // a solution name is legitimately glued to digits and underscores in artefact filenames
    // (AcmeBank_1_0_0_0.zip), where a boundary never fires.
    static readonly HashSet<string> s_neverIdentifying = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "master", "develop", "development", "trunk", "release", "feature", "hotfix",
        "solution", "plugins", "webresources", "source", "test", "tests", "docs", "temp",
    };

    public static FlowlineScrubber Current { get; private set; } = new([]);

    /// <summary>
    /// Whether this scrubber can hash at all. An HMAC under an empty key is a public function, so a
    /// run whose salt could not be loaded must not pretend its values are protected.
    /// </summary>
    public bool HasSalt => salt.Length > 0;

    public static void Initialize(byte[] salt) => Current = new FlowlineScrubber(salt);

    /// <summary>Registers a value known to identify the user or their client, to be hashed wherever it appears.</summary>
    public void AddKnownValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinKnownValueLength) return;

        var trimmed = value.Trim();
        if (s_neverIdentifying.Contains(trimmed)) return;
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
            result = s_dataverseHost.Replace(result, m => Hash(m.Value, salt));
            // Idempotent: the prefix is re-emitted, so /home/<hash> matches this rule again on a second
            // pass. A rendered exception really is scrubbed twice — once in the handler, once more by
            // the enricher and the processor — and without this an already-hashed segment would hash
            // again and stop matching the same value scrubbed once somewhere else.
            result = s_homeUser.Replace(result, m =>
            {
                var user = m.Groups["user"].Value;
                return m.Groups["prefix"].Value + (IsAlreadyHashed(user) ? user : Hash(user, salt));
            });

            // Longest first. Registration order is solution, branch, folder, and one of those can be a
            // prefix of another — a solution named AcmeBank inside a folder named AcmeBankCustomizations.
            // Replacing the short one first destroys the match the long one needed, and the remainder
            // ("Customizations") survives in plaintext.
            foreach (var known in _knownValues.OrderByDescending(v => v.Length))
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
    /// <summary>Hashes one URL with this scrubber's salt, for a caller that already knows it has one.</summary>
    public string ScrubUrl(string url) => HashUrl(url, salt);

    internal static string HashUrl(string url, byte[] salt) =>
        HashHex(salt, Encoding.UTF8.GetBytes(url), 8);

    internal static string Hash(string value, byte[] salt) =>
        HashHex(salt, Encoding.UTF8.GetBytes(value.ToLowerInvariant()), 8);

    // Without a salt there is nothing to hash with: HMAC under an empty key is precomputable, and the
    // space of Dataverse hostnames is small enough to enumerate. A fixed token loses the value rather
    // than handing out a reversible one. FlowlineTelemetry refuses to export at all in that state.
    const string Unsalted = "<unsalted>";

    static string HashHex(byte[] salt, byte[] data, int length) =>
        salt.Length == 0
            ? Unsalted
            : Convert.ToHexString(HMACSHA256.HashData(salt, data))[..length].ToLowerInvariant();

    static bool IsAlreadyHashed(string value) =>
        (value.Length == 8 && value.All(Uri.IsHexDigit)) || value == Unsalted;
}
