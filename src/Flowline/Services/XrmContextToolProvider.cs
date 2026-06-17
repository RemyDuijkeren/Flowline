using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Flowline.Services;

public class XrmContextToolProvider
{
    private const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/delegate.xrmcontext/index.json";
    private const string NuGetDownloadUrlTemplate = "https://api.nuget.org/v3-flatcontainer/delegate.xrmcontext/{0}/delegate.xrmcontext.{0}.nupkg";
    private const string ExeName = "XrmContext.exe";

    private readonly HttpClient _httpClient;
    private readonly string _nugetGlobalCache;
    private readonly string _flowlineCache;

    public XrmContextToolProvider(HttpClient httpClient)
        : this(
            httpClient,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "delegate.xrmcontext"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flowline", "tools", "xrmcontext"))
    {
    }

    protected XrmContextToolProvider(HttpClient httpClient, string nugetGlobalCache, string flowlineCache)
    {
        _httpClient = httpClient;
        _nugetGlobalCache = nugetGlobalCache;
        _flowlineCache = flowlineCache;
    }

    public async Task<string> GetExePathAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new FlowlineException(ExitCode.BuildFailed,
                "XrmContext generator requires Windows. Use --generator pac on Linux/macOS.");

        if (Directory.Exists(_nugetGlobalCache))
        {
            var exePath = FindExeInVersionedDir(_nugetGlobalCache);
            if (exePath != null)
                return exePath;
        }

        if (Directory.Exists(_flowlineCache))
        {
            var exePath = FindExeInVersionedDir(_flowlineCache);
            if (exePath != null)
                return exePath;
        }

        return await DownloadAndExtractAsync(cancellationToken);
    }

    private static string? FindExeInVersionedDir(string baseDir)
    {
        foreach (var versionDir in Directory.EnumerateDirectories(baseDir))
        {
            var toolsDir = Path.Combine(versionDir, "tools");
            if (!Directory.Exists(toolsDir))
                continue;

            var exePath = Directory
                .EnumerateFiles(toolsDir, ExeName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (exePath != null)
                return exePath;
        }

        return null;
    }

    private async Task<string> DownloadAndExtractAsync(CancellationToken cancellationToken)
    {
        var version = await ResolveLatestVersionAsync(cancellationToken);

        var downloadUrl = string.Format(NuGetDownloadUrlTemplate, version);
        using var downloadResponse = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!downloadResponse.IsSuccessStatusCode)
            throw new FlowlineException(ExitCode.BuildFailed,
                $"Failed to download XrmContext from NuGet: {(int)downloadResponse.StatusCode}. Check your internet connection and retry.");

        var extractDir = Path.Combine(_flowlineCache, version);
        Directory.CreateDirectory(extractDir);

        await using var stream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.Combine(extractDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            if (entry.Name.Length > 0)
                entry.ExtractToFile(destPath, overwrite: true);
        }

        var exePath = Directory
            .EnumerateFiles(extractDir, ExeName, SearchOption.AllDirectories)
            .FirstOrDefault();

        if (exePath == null)
            throw new FlowlineException(ExitCode.BuildFailed,
                "XrmContext.exe not found in downloaded package. Report this at github.com/RemyDuijkeren/Flowline/issues.");

        return exePath;
    }

    private async Task<string> ResolveLatestVersionAsync(CancellationToken cancellationToken)
    {
        using var indexResponse = await _httpClient.GetAsync(NuGetIndexUrl, cancellationToken);

        if (!indexResponse.IsSuccessStatusCode)
            throw new FlowlineException(ExitCode.BuildFailed,
                $"Failed to download XrmContext from NuGet: {(int)indexResponse.StatusCode}. Check your internet connection and retry.");

        await using var indexStream = await indexResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken);

        var version = doc.RootElement
            .GetProperty("versions")
            .EnumerateArray()
            .Select(v => v.GetString() ?? "")
            .Where(v => !v.Contains('-'))
            .LastOrDefault();

        if (string.IsNullOrEmpty(version))
            throw new FlowlineException(ExitCode.BuildFailed,
                "Failed to download XrmContext from NuGet: no stable release found. Check your internet connection and retry.");

        return version;
    }
}
