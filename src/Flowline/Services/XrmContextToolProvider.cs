using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class XrmContextToolProvider
{
    private const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/delegate.xrmcontext/index.json";
    private const string NuGetDownloadUrlTemplate = "https://api.nuget.org/v3-flatcontainer/delegate.xrmcontext/{0}/delegate.xrmcontext.{0}.nupkg";
    private const string ExeName = "XrmContext.exe";

    private readonly HttpClient _httpClient;
    private readonly IAnsiConsole _console;
    private readonly FlowlineRuntimeOptions _runtimeOptions;
    private readonly string _nugetGlobalCache;
    private readonly string _flowlineCache;

    public XrmContextToolProvider(HttpClient httpClient, IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions)
        : this(
            httpClient, console, runtimeOptions,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "delegate.xrmcontext"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flowline", "tools", "xrmcontext3"))
    {
    }

    protected XrmContextToolProvider(HttpClient httpClient, IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, string nugetGlobalCache, string flowlineCache)
    {
        _httpClient = httpClient;
        _console = console;
        _runtimeOptions = runtimeOptions;
        _nugetGlobalCache = nugetGlobalCache;
        _flowlineCache = flowlineCache;
    }

    protected virtual bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public virtual async Task<string> GetExePathAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWindows())
            throw new FlowlineException(ExitCode.ConfigInvalid,
                "XrmContext generator requires Windows. Use --generator pac on Linux/macOS.");

        if (Directory.Exists(_nugetGlobalCache))
        {
            var exePath = FindExeInVersionedDir(_nugetGlobalCache);
            if (exePath != null)
            {
                _console.Verbose($"XrmContext: {exePath}", _runtimeOptions.IsVerbose);
                return exePath;
            }
        }

        if (Directory.Exists(_flowlineCache))
        {
            var exePath = FindExeInVersionedDir(_flowlineCache);
            if (exePath != null)
            {
                _console.Verbose($"XrmContext: {exePath}", _runtimeOptions.IsVerbose);
                return exePath;
            }
        }

        return await DownloadAndExtractAsync(cancellationToken);
    }

    private static string? FindExeInVersionedDir(string baseDir)
    {
        var dirs = Directory.EnumerateDirectories(baseDir)
            .OrderByDescending(d =>
            {
                Version.TryParse(Path.GetFileName(d), out var v);
                return v ?? new Version(0, 0);
            });

        foreach (var versionDir in dirs)
        {
            // prefer content/XrmContext/ — has all DLLs alongside the exe
            var contentExe = Path.Combine(versionDir, "content", "XrmContext", ExeName);
            if (File.Exists(contentExe))
                return contentExe;

            // fallback for non-standard layouts
            var exePath = Directory
                .EnumerateFiles(versionDir, ExeName, SearchOption.AllDirectories)
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
        var extractDir = Path.Combine(_flowlineCache, version);

        var tempExtractDir = extractDir + "~";
        string? exePath = null;
        try
        {
            await _console.Status().FlowlineSpinner().StartAsync(
                $"Downloading XrmContext [bold]{version}[/]...",
                async _ =>
                {
                    using var downloadResponse = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (!downloadResponse.IsSuccessStatusCode)
                        throw new FlowlineException(ExitCode.BuildFailed,
                            $"Failed to download XrmContext from NuGet: {(int)downloadResponse.StatusCode}. Check your internet connection and retry.");

                    Directory.CreateDirectory(tempExtractDir);

                    await using var stream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                    var canonicalTemp = Path.GetFullPath(tempExtractDir) + Path.DirectorySeparatorChar;
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.FullName.StartsWith("content/XrmContext/", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var destPath = Path.GetFullPath(Path.Combine(tempExtractDir, relativePath));

                        if (!destPath.StartsWith(canonicalTemp, StringComparison.OrdinalIgnoreCase))
                            continue; // skip path traversal attempts

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                        if (entry.Name.Length > 0)
                            entry.ExtractToFile(destPath, overwrite: true);
                    }

                    exePath = Directory
                        .EnumerateFiles(tempExtractDir, ExeName, SearchOption.AllDirectories)
                        .FirstOrDefault();
                });
        }
        catch
        {
            if (Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, recursive: true);
            throw;
        }

        if (exePath == null)
        {
            if (Directory.Exists(tempExtractDir))
                Directory.Delete(tempExtractDir, recursive: true);
            throw new FlowlineException(ExitCode.BuildFailed,
                "XrmContext.exe not found in downloaded package. Report this at github.com/RemyDuijkeren/Flowline/issues.");
        }

        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        Directory.Move(tempExtractDir, extractDir);
        exePath = exePath.Replace(tempExtractDir, extractDir, StringComparison.OrdinalIgnoreCase);

        _console.Ok("XrmContext ready");
        _console.Verbose($"Extracted to: {extractDir}", _runtimeOptions.IsVerbose);
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
