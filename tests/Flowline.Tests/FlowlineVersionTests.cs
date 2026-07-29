using System.Reflection;
using Flowline.Utils;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests;

// `flowline --version` and the welcome screen used AssemblyFileVersion, which MinVer stamps identically
// for every prerelease of the same release (0.13.1-alpha.0.2 and 0.13.1-alpha.0.7 both report 0.13.1.0).
// That made it impossible to tell whether a locally packed build had actually replaced the installed
// tool — the exact check the release/test workflow depends on.
public class FlowlineVersionTests
{
    [Fact]
    public void Display_UsesInformationalVersion_NotFourPartFileVersion()
    {
        var assembly = typeof(FlowlineVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = informational.Split('+')[0];

        FlowlineVersion.Display.Should().Be(expected);
    }

    [Fact]
    public void Display_DropsBuildMetadataSuffix()
    {
        FlowlineVersion.Display.Should().NotContain("+");
    }

    [Fact]
    public void Display_IsNotEmpty()
    {
        FlowlineVersion.Display.Should().NotBeNullOrWhiteSpace();
    }
}
