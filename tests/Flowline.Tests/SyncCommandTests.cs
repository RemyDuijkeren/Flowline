using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class SyncCommandTests
{
    [Fact]
    public void Settings_Force_ShouldDefaultToFalse()
    {
        new SyncCommand.Settings().Force.Should().BeFalse();
    }
}
