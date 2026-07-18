using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.WebResources;
using FluentAssertions;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.WebResources;

public class WebResourcePlannerTests
{
    static readonly DataverseSolutionInfo Solution = new(Guid.NewGuid(), "MySolution", "my", false, null);

    static WebResourceSyncSnapshot SnapshotWithLocal(LocalWebResource local) =>
        new(
            Solution,
            new Dictionary<string, LocalWebResource>(StringComparer.OrdinalIgnoreCase) { [local.Name] = local },
            new Dictionary<string, DataverseWebResource>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DataverseWebResource>(StringComparer.OrdinalIgnoreCase));

    static LocalWebResource UnknownLocal(string name) =>
        new(name, name, name, name, WebResourceType.Unknown, "eA==", []);

    [Fact]
    public void Plan_StillUnknownAfterBothTiers_ThrowsFlowlineExceptionWithValidationFailedExitCode()
    {
        var console = new TestConsole();
        var planner = new WebResourcePlanner(console);
        var snapshot = SnapshotWithLocal(UnknownLocal("my_MySolution/mystery"));

        var act = () => planner.Plan(snapshot);

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Fact]
    public void Plan_StillUnknownAfterBothTiers_ErrorMentionsBothTiers()
    {
        var console = new TestConsole();
        var planner = new WebResourcePlanner(console);
        var snapshot = SnapshotWithLocal(UnknownLocal("my_MySolution/mystery"));

        var act = () => planner.Plan(snapshot);

        act.Should().Throw<FlowlineException>();
        console.Output.Should().Contain("metadata lookup").And.Contain("content sniffing");
    }
}
