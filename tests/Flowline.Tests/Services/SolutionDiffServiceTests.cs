using Flowline.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flowline.Tests.Services;

public class SolutionDiffServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_DoesNotThrow()
    {
        var svc = new SolutionDiffService(NullLogger<SolutionDiffService>.Instance);
        Assert.NotNull(svc);
    }

    [Fact]
    public async Task ComputeAsync_EmptyDirectory_ReturnsTotalFilesZero()
    {
        var svc = new SolutionDiffService(NullLogger<SolutionDiffService>.Instance);
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var result = await svc.ComputeAsync(tmpDir, tmpDir, false, CancellationToken.None);
            Assert.Equal(0, result.TotalFiles);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
