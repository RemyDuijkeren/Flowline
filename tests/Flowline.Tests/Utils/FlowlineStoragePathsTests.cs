using FluentAssertions;
using Flowline.Utils;

namespace Flowline.Tests.Utils;

public class FlowlineStoragePathsTests
{
    [Fact]
    public void GetStorageRoot_ReturnsNonNullPathEndingWithFlowline()
    {
        var root = FlowlineStoragePaths.GetStorageRoot();

        root.Should().NotBeNullOrWhiteSpace();
        root.Should().EndWith("Flowline");
    }

    [Fact]
    public void GetLogsPath_ReturnsPathEndingWithLogsTimestampAndExtension()
    {
        var runTime = new DateTimeOffset(2026, 1, 15, 8, 26, 44, TimeSpan.Zero);

        var path = FlowlineStoragePaths.GetLogsPath(runTime);

        path.Should().EndWith(Path.Combine("logs", "2026-01-15T082644Z.log"));
    }

    // U2: one form-event identity cache file per environment (form-events/ subfolder), sanitized so an
    // http(s) environment URL is filesystem-safe on every platform.
    [Fact]
    public void GetFormEventCachePath_ReturnsPathUnderFormEventsSubfolderWithJsonExtension()
    {
        var path = FlowlineStoragePaths.GetFormEventCachePath("https://contoso.crm.dynamics.com");

        path.Should().Contain($"{Path.DirectorySeparatorChar}form-events{Path.DirectorySeparatorChar}");
        path.Should().EndWith(".json");
    }

    [Fact]
    public void GetFormEventCachePath_SameEnvironmentUrl_ReturnsSameStablePath()
    {
        const string url = "https://contoso.crm.dynamics.com";

        var first = FlowlineStoragePaths.GetFormEventCachePath(url);
        var second = FlowlineStoragePaths.GetFormEventCachePath(url);

        first.Should().Be(second);
    }

    [Fact]
    public void GetFormEventCachePath_SanitizesSchemeAndUnsafeChars()
    {
        var path = FlowlineStoragePaths.GetFormEventCachePath("https://contoso.crm.dynamics.com:443/");

        Path.GetFileNameWithoutExtension(path).Should().Be("contoso_crm_dynamics_com_443_");
    }
}
