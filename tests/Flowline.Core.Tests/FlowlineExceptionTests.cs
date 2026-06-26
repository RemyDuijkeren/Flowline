using Flowline;

namespace Flowline.Core.Tests;

public class FlowlineExceptionTests
{
    [Fact]
    public void WithSubprocessBuffer_NonVerbose_SetsBothSubprocessOutputAndDetail()
    {
        var ex = new FlowlineException("test error");
        var lines = new[] { "line 1", "line 2" };

        ex.WithSubprocessBuffer(lines, isVerbose: false);

        Assert.Equal(lines, ex.SubprocessOutput);
        Assert.NotNull(ex.Detail);
    }

    [Fact]
    public void WithSubprocessBuffer_Verbose_SetsSubprocessOutputButLeavesDetailNull()
    {
        var ex = new FlowlineException("test error");
        var lines = new[] { "line 1", "line 2" };

        ex.WithSubprocessBuffer(lines, isVerbose: true);

        Assert.Equal(lines, ex.SubprocessOutput);
        Assert.Null(ex.Detail);
    }
}
