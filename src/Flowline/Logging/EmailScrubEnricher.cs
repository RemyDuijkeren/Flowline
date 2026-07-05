using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Flowline.Logging;

public sealed class EmailScrubEnricher : ILogEventEnricher
{
    static readonly Regex s_email =
        new(@"([\w.+-]+)@([\w-]+(?:\.[\w-]+)+)", RegexOptions.Compiled);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (key, value) in logEvent.Properties.ToList())
        {
            if (value is ScalarValue { Value: string str } && s_email.IsMatch(str))
            {
                var scrubbed = s_email.Replace(str, m => $"usr_{Hash(m.Groups[1].Value)}.tnt_{Hash(m.Groups[2].Value)}");
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, scrubbed));
            }
        }
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..8].ToLowerInvariant();
}
