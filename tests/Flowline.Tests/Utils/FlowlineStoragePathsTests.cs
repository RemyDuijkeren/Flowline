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
    public void GetRunsPath_ReturnsPathEndingWithRunsDateAndExtension()
    {
        var date = new DateOnly(2026, 1, 15);

        var path = FlowlineStoragePaths.GetRunsPath(date);

        path.Should().EndWith(Path.Combine("runs", "2026-01-15.jsonl"));
    }

    [Fact]
    public void GetLogsPath_ReturnsPathEndingWithLogsDateAndExtension()
    {
        var date = new DateOnly(2026, 1, 15);

        var path = FlowlineStoragePaths.GetLogsPath(date);

        path.Should().EndWith(Path.Combine("logs", "2026-01-15.log"));
    }
}
