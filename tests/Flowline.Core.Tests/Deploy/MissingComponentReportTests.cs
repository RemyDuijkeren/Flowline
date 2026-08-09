using FluentAssertions;
using Flowline.Core.Deploy;

namespace Flowline.Core.Tests.Deploy;

public class MissingComponentReportTests : IDisposable
{
    const string TargetUrl = "https://example.crm.dynamics.com";
    const string SolutionName = "MySolution";

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

        var reportPath = MissingComponentReport.Write(unreachable, TargetUrl, SolutionName, [Result(1)]);

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

        var act = () => MissingComponentReport.ClearReport(unreachable, TargetUrl);

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

        var reportPath = MissingComponentReport.Write(_packagePath, TargetUrl, SolutionName, results);

        reportPath.Should().NotBeNull();
        var content = File.ReadAllText(reportPath!);
        for (var i = 1; i <= 12; i++)
            content.Should().Contain($"new_field{i}");
    }

    [Fact]
    public void ClearReport_PreExistingReport_RemovesIt()
    {
        var reportPath = MissingComponentReport.GetReportPath(_packagePath, TargetUrl);
        File.WriteAllText(reportPath, "stale report from an earlier blocked run");

        MissingComponentReport.ClearReport(_packagePath, TargetUrl);

        File.Exists(reportPath).Should().BeFalse();
    }

    [Fact]
    public void ClearReport_NoExistingReport_IsNoOpAndDoesNotThrow()
    {
        var act = () => MissingComponentReport.ClearReport(_packagePath, TargetUrl);

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

    // FIX 6: the block message must offer the same escape hatch as the transport-failure message —
    // a developer who believes the block is wrong needs a route out.
    [Fact]
    public void RenderFailureMessage_NamesTheSkipFlag()
    {
        var message = MissingComponentReport.RenderFailureMessage([Result(1)], @"C:\artifacts\missing-components.txt");

        message.Should().Contain("--skip-component-check");
    }

    // Exact truncation boundary: 5 shows all with no "more" line, 6 shows five plus one "more" line.
    [Fact]
    public void RenderFailureMessage_ExactlyFive_RendersAllWithNoMoreLine()
    {
        var results = Enumerable.Range(1, 5).Select(Result).ToList();

        var message = MissingComponentReport.RenderFailureMessage(results, @"C:\artifacts\missing-components.txt");

        for (var i = 1; i <= 5; i++)
            message.Should().Contain($"new_field{i}");
        message.Should().NotContain("more");
    }

    [Fact]
    public void RenderFailureMessage_ExactlySix_RendersFivePlusOneMoreLine()
    {
        var results = Enumerable.Range(1, 6).Select(Result).ToList();

        var message = MissingComponentReport.RenderFailureMessage(results, @"C:\artifacts\missing-components.txt");

        for (var i = 1; i <= 5; i++)
            message.Should().Contain($"new_field{i}");
        message.Should().NotContain("new_field6");
        message.Should().Contain("...and 1 more");
    }

    // An unmapped component type must never leak the raw integer into the rendered line. Uses
    // digit-free literals (not the Result(i) helper, whose names end in digits) so NotMatchRegex(@"\d")
    // only fails on the type number leaking through.
    [Fact]
    public void FormatComponentLine_UnmappedComponentType_RendersWithNoNumericArtifact()
    {
        var result = new MissingComponentResult("new_field", "Field", "ContosoSolution", 9999, "new_entity", "Entity");

        var line = MissingComponentReport.FormatComponentLine(result);

        line.Should().NotMatchRegex(@"\d");
    }

    // FIX 1: two different targets sharing the same package must resolve to different report paths,
    // and clearing one must not touch the other's report.
    [Fact]
    public void GetReportPath_DifferentTargets_ResolveToDifferentPaths()
    {
        var prodPath = MissingComponentReport.GetReportPath(_packagePath, "https://contoso.crm4.dynamics.com");
        var testPath = MissingComponentReport.GetReportPath(_packagePath, "https://contoso-test.crm4.dynamics.com");

        prodPath.Should().NotBe(testPath);
    }

    [Fact]
    public void ClearReport_TwoTargets_ClearingOneLeavesTheOtherOnDisk()
    {
        const string prodUrl = "https://contoso.crm4.dynamics.com";
        const string testUrl = "https://contoso-test.crm4.dynamics.com";

        MissingComponentReport.Write(_packagePath, prodUrl, SolutionName, [Result(1)]);
        var testReportPath = MissingComponentReport.Write(_packagePath, testUrl, SolutionName, [Result(2)]);

        MissingComponentReport.ClearReport(_packagePath, prodUrl);

        File.Exists(MissingComponentReport.GetReportPath(_packagePath, prodUrl)).Should().BeFalse();
        File.Exists(testReportPath!).Should().BeTrue();
    }

    // FIX 5: a relative/bare-filename package path must still resolve to an absolute report path.
    [Fact]
    public void GetReportPath_RelativePackagePath_ResolvesToAnAbsolutePath()
    {
        var reportPath = MissingComponentReport.GetReportPath("sol.zip", TargetUrl);

        Path.IsPathRooted(reportPath).Should().BeTrue();
    }

    // FIX 1: the written report carries the solution and target it was checked against, so a reader
    // can tell what run produced it.
    [Fact]
    public void Write_IncludesSolutionNameAndTargetUrlInHeader()
    {
        var reportPath = MissingComponentReport.Write(_packagePath, TargetUrl, SolutionName, [Result(1)]);

        var content = File.ReadAllText(reportPath!);
        content.Should().Contain(SolutionName);
        content.Should().Contain(TargetUrl);
    }
}
