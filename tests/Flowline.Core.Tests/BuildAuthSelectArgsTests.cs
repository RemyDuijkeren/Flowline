using Flowline.Core.Environments;
using Flowline.Core.Models;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class BuildAuthSelectArgsTests
{
    [Fact]
    public void BuildAuthSelectArgs_ProfileHasName_ReturnsNameArg()
    {
        var profile = new PacProfile { Name = "MyProfile", Kind = "DATAVERSE" };
        var allProfiles = new List<PacProfile> { profile };

        var result = ProfileResolutionService.BuildAuthSelectArgs(profile, allProfiles);

        result.ArgName.Should().Be("--name");
        result.ArgValue.Should().Be("MyProfile");
    }

    [Fact]
    public void BuildAuthSelectArgs_ProfileHasNoName_ReturnsIndexArgAtItsPosition()
    {
        var first = new PacProfile { Kind = "DATAVERSE", User = "a@contoso.com" };
        var target = new PacProfile { Kind = "DATAVERSE", User = "b@contoso.com" };
        var allProfiles = new List<PacProfile> { first, target };

        var result = ProfileResolutionService.BuildAuthSelectArgs(target, allProfiles);

        result.ArgName.Should().Be("--index");
        result.ArgValue.Should().Be("2");
    }

    [Fact]
    public void BuildAuthSelectArgs_ProfileNameIsWhitespace_FallsBackToIndex()
    {
        var profile = new PacProfile { Name = "   ", Kind = "DATAVERSE" };
        var allProfiles = new List<PacProfile> { profile };

        var result = ProfileResolutionService.BuildAuthSelectArgs(profile, allProfiles);

        result.ArgName.Should().Be("--index");
        result.ArgValue.Should().Be("1");
    }

    [Fact]
    public void BuildAuthSelectArgs_UnnamedProfileNotInList_Throws()
    {
        var profile = new PacProfile { Kind = "DATAVERSE" };
        var allProfiles = new List<PacProfile>();

        var act = () => ProfileResolutionService.BuildAuthSelectArgs(profile, allProfiles);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.NotAuthenticated);
    }
}
