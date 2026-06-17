using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Flowline.Services;

namespace Flowline.Tests;

public class XrmContextToolProviderTests : IDisposable
{
    readonly string _tempRoot;

    public XrmContextToolProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"XrmContextTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    static HttpClient MakeHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new FakeHttpHandler(handler));

    static HttpClient MakeDownloadClient(string version = "3.0.1")
    {
        // version is the stable latest; include a preview and an older stable to exercise filtering
        var versionJson = $$$"""{"versions":["2.9.0","{{{version}}}-preview1","{{{version}}}"] }""";

        var nupkg = BuildFakeNupkg(version, includeExe: true);

        return MakeHttpClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("index.json"))
                return Ok(versionJson, "application/json");
            return Ok(nupkg, "application/octet-stream");
        });
    }

    static HttpResponseMessage Ok(string body, string contentType)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Content = new StringContent(body, Encoding.UTF8, contentType);
        return resp;
    }

    static HttpResponseMessage Ok(byte[] body, string contentType)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Content = new ByteArrayContent(body);
        resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return resp;
    }

    static byte[] BuildFakeNupkg(string version, bool includeExe)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, $"delegate.xrmcontext.{version}.nuspec", "<package/>");

            if (includeExe)
            {
                WriteEntry(zip, "tools/net462/XrmContext.exe", "fake-exe");
                WriteEntry(zip, "tools/net462/FSharp.Core.dll", "fake-dll");
            }
        }
        return ms.ToArray();
    }

    static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    XrmContextToolProvider MakeProvider(HttpClient? client = null) =>
        new(client ?? MakeDownloadClient());

    // ── NuGet global cache ───────────────────────────────────────────────────

    [Fact]
    public async Task GetExePathAsync_ReturnsPath_WhenExeInNuGetCache()
    {
        var nugetCache = Path.Combine(_tempRoot, "nuget-cache", "delegate.xrmcontext");
        var exePath = Path.Combine(nugetCache, "3.0.1", "tools", "net462", "XrmContext.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "fake");

        var provider = new TestableXrmContextToolProvider(
            MakeHttpClient(_ => throw new Exception("should not be called")),
            nugetGlobalCache: nugetCache,
            flowlineCache: Path.Combine(_tempRoot, "flowline-cache"));

        var result = await provider.GetExePathAsync();

        result.Should().Be(exePath);
    }

    // ── Flowline profile cache ───────────────────────────────────────────────

    [Fact]
    public async Task GetExePathAsync_ReturnsPath_WhenExeInFlowlineCache()
    {
        var flowlineCache = Path.Combine(_tempRoot, "flowline-cache");
        var exePath = Path.Combine(flowlineCache, "3.0.1", "tools", "net462", "XrmContext.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, "fake");

        var nugetCache = Path.Combine(_tempRoot, "nonexistent-nuget");

        var provider = new TestableXrmContextToolProvider(
            MakeHttpClient(_ => throw new Exception("should not be called")),
            nugetGlobalCache: nugetCache,
            flowlineCache: flowlineCache);

        var result = await provider.GetExePathAsync();

        result.Should().Be(exePath);
    }

    // ── Download path ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetExePathAsync_DownloadsExtracts_WhenNoCacheHit()
    {
        var flowlineCache = Path.Combine(_tempRoot, "flowline-cache");
        var nugetCache = Path.Combine(_tempRoot, "nonexistent-nuget");

        var provider = new TestableXrmContextToolProvider(
            MakeDownloadClient("3.0.1"),
            nugetGlobalCache: nugetCache,
            flowlineCache: flowlineCache);

        var result = await provider.GetExePathAsync();

        result.Should().EndWith("XrmContext.exe");
        File.Exists(result).Should().BeTrue();
    }

    [Fact]
    public async Task GetExePathAsync_ExtractsBundledDlls_AlongsideExe()
    {
        var flowlineCache = Path.Combine(_tempRoot, "flowline-cache");
        var nugetCache = Path.Combine(_tempRoot, "nonexistent-nuget");

        var provider = new TestableXrmContextToolProvider(
            MakeDownloadClient("3.0.1"),
            nugetGlobalCache: nugetCache,
            flowlineCache: flowlineCache);

        await provider.GetExePathAsync();

        var exeDir = Path.Combine(flowlineCache, "3.0.1", "tools", "net462");
        File.Exists(Path.Combine(exeDir, "FSharp.Core.dll")).Should().BeTrue();
    }

    [Fact]
    public async Task GetExePathAsync_FiltersPreRelease_PicksStableVersion()
    {
        var flowlineCache = Path.Combine(_tempRoot, "flowline-cache");
        var nugetCache = Path.Combine(_tempRoot, "nonexistent-nuget");

        // versions: ["2.9.0-preview1", "2.9.0", "3.0.1-beta"] — stable latest is 2.9.0
        var versionJson = """{"versions":["2.9.0-preview1","2.9.0","3.0.1-beta"]}""";
        var nupkg = BuildFakeNupkg("2.9.0", includeExe: true);

        var client = MakeHttpClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("index.json"))
                return Ok(versionJson, "application/json");
            // verify correct version used in download URL
            req.RequestUri.AbsolutePath.Should().Contain("2.9.0");
            return Ok(nupkg, "application/octet-stream");
        });

        var provider = new TestableXrmContextToolProvider(client, nugetCache, flowlineCache);
        var result = await provider.GetExePathAsync();

        result.Should().Contain("2.9.0");
    }

    // ── HTTP failure ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetExePathAsync_ThrowsBuildFailed_OnHttpError()
    {
        var flowlineCache = Path.Combine(_tempRoot, "flowline-cache");
        var nugetCache = Path.Combine(_tempRoot, "nonexistent-nuget");

        var client = MakeHttpClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var provider = new TestableXrmContextToolProvider(client, nugetCache, flowlineCache);

        var act = () => provider.GetExePathAsync();

        await act.Should().ThrowAsync<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.BuildFailed &&
                        e.Message.Contains("500"));
    }

    // ── Missing exe in zip ───────────────────────────────────────────────────

    [Fact]
    public async Task GetExePathAsync_ThrowsBuildFailed_WhenZipHasNoExe()
    {
        var flowlineCache = Path.Combine(_tempRoot, "flowline-cache");
        var nugetCache = Path.Combine(_tempRoot, "nonexistent-nuget");

        var versionJson = """{"versions":["3.0.1"]}""";
        var nupkg = BuildFakeNupkg("3.0.1", includeExe: false);

        var client = MakeHttpClient(req =>
            req.RequestUri!.AbsolutePath.EndsWith("index.json")
                ? Ok(versionJson, "application/json")
                : Ok(nupkg, "application/octet-stream"));

        var provider = new TestableXrmContextToolProvider(client, nugetCache, flowlineCache);

        var act = () => provider.GetExePathAsync();

        await act.Should().ThrowAsync<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.BuildFailed &&
                        e.Message.Contains("XrmContext.exe not found"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    /// <summary>
    /// Subclass that overrides the two OS-specific paths so tests can inject temp directories.
    /// The OS guard is not exercised here (tests run on the current OS); it is verified by inspection.
    /// </summary>
    private class TestableXrmContextToolProvider(
        HttpClient httpClient,
        string nugetGlobalCache,
        string flowlineCache)
        : XrmContextToolProvider(httpClient, nugetGlobalCache, flowlineCache);
}
