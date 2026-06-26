using FluentAssertions;
using Flowline.Utils;

namespace Flowline.Tests.Utils;

public class SubprocessBufferTests
{
    [Fact]
    public void Append_WhenMoreThan50Lines_DropsOldestLine()
    {
        var buffer = new SubprocessBuffer();
        for (var i = 1; i <= 51; i++)
            buffer.Append($"line {i}");

        buffer.Lines.Should().HaveCount(50);
        buffer.Lines[0].Should().Be("line 2");
    }

    [Fact]
    public void Lines_WhenFewerThan50_ReturnsAllLines()
    {
        var buffer = new SubprocessBuffer();
        buffer.Append("line 1");
        buffer.Append("line 2");
        buffer.Append("line 3");

        buffer.Lines.Should().HaveCount(3);
        buffer.Lines.Should().Equal("line 1", "line 2", "line 3");
    }
}
