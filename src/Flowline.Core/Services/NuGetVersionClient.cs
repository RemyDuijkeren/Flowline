using System.Text.Json;

namespace Flowline.Core.Services;

/// <summary>
/// Fetches the published version list for a package from the NuGet flat-container index. Every
/// failure — offline, timeout, non-success status, unparsable body — is caught and treated as "no
/// verdict": the caller gets nothing back, never an exception.
/// </summary>
public class NuGetVersionClient(HttpClient httpClient, TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <summary>Raw version strings published for <paramref name="packageId"/>, in the order the
    /// index response carried them, or <c>null</c> on any failure.</summary>
    public async Task<IReadOnlyList<string>?> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            using var response = await httpClient.GetAsync(
                $"https://api.nuget.org/v3-flatcontainer/{packageId}/index.json", cts.Token);

            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

            return doc.RootElement
                .GetProperty("versions")
                .EnumerateArray()
                .Select(v => v.GetString())
                .Where(v => v != null)
                .Select(v => v!)
                .ToList();
        }
        catch
        {
            return null;
        }
    }
}
