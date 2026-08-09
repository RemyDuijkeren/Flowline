using FluentAssertions;
using Flowline.Core.Deploy;

namespace Flowline.Core.Tests.Deploy;

public class MissingComponentReportTests : IDisposable
{
    readonly string _packagePath;

    public MissingComponentReportTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flowline-mcr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _packagePath = Path.Combine(dir, "MySolution_1_0_0_0.zip");
        File.WriteAllBytes(_packagePath, [1, 2, 3]);
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_packagePath)!;
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    static MissingComponentResult Result(int i) =>
        new($"new_field{i}", $"Field {i}", $"Solution{i}", 2, $"new_entity{i}", $"Entity {i}");

    // A report the gate can't write must not mask the verdict — the components still block the deploy.
    [Fact]
    public void Write_UnwritableLocation_ReturnsNullRatherThanThrowing()
    {
        var unreachable = Path.Combine(Path.GetTempPath(), $"flowline-absent-{Guid.NewGuid():N}", "Sln.zip");

        var reportPath = MissingComponentReport.Write(unreachable, [Result(1)]);

        reportPath.Should().BeNull();
    }

    [Fact]
    public void RenderFailureMessage_NoReportPath_StillNamesTheComponentsAndSaysWhy()
    {
        var message = MissingComponentReport.RenderFailureMessage([Result(1)], reportPath: null);

        message.Should().Contain("new_field1");
        message.Should().Contain("Couldn't write the full report");
        message.Should().NotContain("Full list:");
    }

    [Fact]
    public void ClearReport_UnreachableDirectory_DoesNotThrow()
    {
        var unreachable = Path.Combine(Path.GetTempPath(), $"flowline-absent-{Guid.NewGuid():N}", "Sln.zip");

        var act = () => MissingComponentReport.ClearReport(unreachable);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderFailureMessage_TwelveComponents_ShowsOnlyFirstFive()
    {
        var results = Enumerable.Range(1, 12).Select(Result).ToList();

        var message = MissingComponentReport.RenderFailureMessage(results, @"C:\artifacts\missing-components.txt");

        for (var i = 1; i <= 5; i++)
            message.Should().Contain($"new_field{i}");
        for (var i = 6; i <= 12; i++)
            message.Should().NotContain($"new_field{i}");
    }

    [Fact]
    public void Write_TwelveComponents_WritesAllTwelveToFile()
    {
        var results = Enumerable.Range(1, 12).Select(Result).ToList();

        var reportPath = MissingComponentReport.Write(_packagePath, results);

        reportPath.Should().NotBeNull();
        var content = File.ReadAllText(reportPath!);
        for (var i = 1; i <= 12; i++)
            content.Should().Contain($"new_field{i}");
    }

    [Fact]
    public void ClearReport_PreExistingReport_RemovesIt()
    {
        var reportPath = MissingComponentReport.GetReportPath(_packagePath);
        File.WriteAllText(reportPath, "stale report from an earlier blocked run");

        MissingComponentReport.ClearReport(_packagePath);

        File.Exists(reportPath).Should().BeFalse();
    }

    [Fact]
    public void ClearReport_NoExistingReport_IsNoOpAndDoesNotThrow()
    {
        var act = () => MissingComponentReport.ClearReport(_packagePath);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderFailureMessage_ComponentMissingOwningSolution_RendersWithoutEmptyFieldArtifactOrGuid()
    {
        var result = new MissingComponentResult("new_field", "Field", null, 2, "new_entity", "Entity");

        var message = MissingComponentReport.RenderFailureMessage([result], @"C:\artifacts\missing-components.txt");

        message.Should().NotContain("()");
        message.Should().NotContain("  in ''"); // no dangling "in ''" for an absent solution
        message.Should().NotMatchRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
    }

    [Fact]
    public void RenderFailureMessage_NamesBothRemedyRoutes_RatherThanPrescribingOne()
    {
        var results = new List<MissingComponentResult> { Result(1) };

        var message = MissingComponentReport.RenderFailureMessage(results, @"C:\artifacts\missing-components.txt");

        message.Should().ContainEquivalentOf("install");
        message.Should().ContainEquivalentOf("sync");
    }

    [Fact]
    public void RenderFailureMessage_FewerThanFive_RendersAllWithNoTruncationPointer()
    {
        var results = Enumerable.Range(1, 3).Select(Result).ToList();

        var message = MissingComponentReport.RenderFailureMessage(results, @"C:\artifacts\missing-components.txt");

        for (var i = 1; i <= 3; i++)
            message.Should().Contain($"new_field{i}");
        message.Should().NotContain("more");
    }
}
