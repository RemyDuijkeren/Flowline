using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Flowline.Logging;

public sealed class UrlScrubEnricher : ILogEventEnricher
{
    static readonly Regex s_url =
        new(@"https?://[^\s""'\]\)\(,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (key, value) in logEvent.Properties.ToList())
        {
            if (value is ScalarValue { Value: string str } && s_url.IsMatch(str))
            {
                var scrubbed = s_url.Replace(str, m => HashUrl(m.Value));
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, scrubbed));
            }
        }
    }

    internal static string HashUrl(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant();
}
