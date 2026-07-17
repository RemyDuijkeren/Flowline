using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;

namespace Flowline.Tests;

public class DeployCommandDtapGateTests
{
    private static ProjectConfig Config(string? prod = null, string? uat = null, string? test = null, string? dev = null) =>
        new() { ProdUrl = prod, UatUrl = uat, TestUrl = test, DevUrl = dev };

    // ── Tier resolution: happy paths ──────────────────────────────────────────

    [Fact]
    public void ResolveDtapGate_ChecksUat_WhenTargetIsProdAndUatConfigured()
    {
        // AE1: ProdUrl + UatUrl → predecessor = UAT
        var config = Config(prod: "https://prod.crm.dynamics.com/", uat: "https://uat.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://prod.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
        result.PredecessorUrl.Should().Be("https://uat.crm.dynamics.com/");
        result.PredecessorLabel.Should().Be("UAT");
    }

    [Fact]
    public void ResolveDtapGate_ChecksTest_WhenTargetIsProdAndOnlyTestConfigured()
    {
        // AE2: ProdUrl + TestUrl (no UatUrl) → predecessor = Test
        var config = Config(prod: "https://prod.crm.dynamics.com/", test: "https://test.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://prod.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
        result.PredecessorUrl.Should().Be("https://test.crm.dynamics.com/");
        result.PredecessorLabel.Should().Be("Test");
    }

    [Fact]
    public void ResolveDtapGate_ChecksDev_WhenTargetIsProdAndOnlyDevConfigured()
    {
        // AE3: ProdUrl + DevUrl (no UatUrl, no TestUrl) → predecessor = Dev
        var config = Config(prod: "https://prod.crm.dynamics.com/", dev: "https://dev.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://prod.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
        result.PredecessorUrl.Should().Be("https://dev.crm.dynamics.com/");
        result.PredecessorLabel.Should().Be("Dev");
    }

    [Fact]
    public void ResolveDtapGate_ChecksTest_WhenTargetIsUatAndTestConfigured()
    {
        var config = Config(uat: "https://uat.crm.dynamics.com/", test: "https://test.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://uat.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
        result.PredecessorUrl.Should().Be("https://test.crm.dynamics.com/");
        result.PredecessorLabel.Should().Be("Test");
    }

    [Fact]
    public void ResolveDtapGate_ChecksDev_WhenTargetIsUatAndOnlyDevConfigured()
    {
        var config = Config(uat: "https://uat.crm.dynamics.com/", dev: "https://dev.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://uat.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
        result.PredecessorUrl.Should().Be("https://dev.crm.dynamics.com/");
        result.PredecessorLabel.Should().Be("Dev");
    }

    [Fact]
    public void ResolveDtapGate_ChecksDev_WhenTargetIsTestAndDevConfigured()
    {
        var config = Config(test: "https://test.crm.dynamics.com/", dev: "https://dev.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://test.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
        result.PredecessorUrl.Should().Be("https://dev.crm.dynamics.com/");
        result.PredecessorLabel.Should().Be("Dev");
    }

    // ── Tier resolution: Dev block ────────────────────────────────────────────

    [Fact]
    public void ResolveDtapGate_ReturnsDevBlock_WhenTargetIsDevUrl()
    {
        // AE4: Target = DevUrl → DevBlock
        var config = Config(prod: "https://prod.crm.dynamics.com/", dev: "https://dev.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://dev.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.DevBlock);
    }

    // ── Tier resolution: skip gate ────────────────────────────────────────────

    [Fact]
    public void ResolveDtapGate_Skips_WhenTargetUrlNotInConfig()
    {
        // AE5: raw URL not matching any configured URL → Skip
        var config = Config(prod: "https://prod.crm.dynamics.com/", uat: "https://uat.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://unknown.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Skip);
    }

    [Fact]
    public void ResolveDtapGate_Skips_WhenNoPredecessorConfiguredBelowTest()
    {
        var config = Config(test: "https://test.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://test.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Skip);
    }

    [Fact]
    public void ResolveDtapGate_Skips_WhenUatHasNoPredecessorConfigured()
    {
        var config = Config(uat: "https://uat.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://uat.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Skip);
    }

    // ── Tier resolution: raw URL matching ────────────────────────────────────

    [Fact]
    public void ResolveDtapGate_MatchesRawProdUrl_WhenProvidedAsTarget()
    {
        // AE6: raw URL matches ProdUrl → gate runs as Prod
        var config = Config(prod: "https://org.crm.dynamics.com/", uat: "https://uat.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://org.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
        result.PredecessorUrl.Should().Be("https://uat.crm.dynamics.com/");
    }

    [Fact]
    public void ResolveDtapGate_IsCaseInsensitive()
    {
        var config = Config(prod: "https://PROD.crm.dynamics.com/", uat: "https://uat.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://prod.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
    }

    [Fact]
    public void ResolveDtapGate_NormalizesTrailingSlash()
    {
        var config = Config(prod: "https://prod.crm.dynamics.com", uat: "https://uat.crm.dynamics.com/");

        var result = DeployCommand.ResolveDtapGate(config, "https://prod.crm.dynamics.com/");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Check);
    }

    // ── Local version reading: happy path ────────────────────────────────────

    [Fact]
    public void ReadLocalSolutionVersion_ReturnsVersion_WhenXmlIsValid()
    {
        using var tmp = new TempPackageFolder("""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <Version>1.2.0.0</Version>
              </SolutionManifest>
            </ImportExportXml>
            """);

        var version = DeployCommand.ReadLocalSolutionVersion(tmp.PackageFolderPath);

        version.Should().Be("1.2.0.0");
    }

    // ── Local version reading: error paths ────────────────────────────────────

    [Fact]
    public void ReadLocalSolutionVersion_Throws_WhenSolutionXmlMissing()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(emptyDir);
        try
        {
            var act = () => DeployCommand.ReadLocalSolutionVersion(emptyDir);
            act.Should().Throw<FlowlineException>();
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    public void ReadLocalSolutionVersion_Throws_WhenVersionElementIsEmpty()
    {
        using var tmp = new TempPackageFolder("""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <Version></Version>
              </SolutionManifest>
            </ImportExportXml>
            """);

        var act = () => DeployCommand.ReadLocalSolutionVersion(tmp.PackageFolderPath);
        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ReadLocalSolutionVersion_Throws_WhenVersionElementAbsent()
    {
        using var tmp = new TempPackageFolder("""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
              </SolutionManifest>
            </ImportExportXml>
            """);

        var act = () => DeployCommand.ReadLocalSolutionVersion(tmp.PackageFolderPath);
        act.Should().Throw<FlowlineException>();
    }

    // ── DtapVersionMatches ────────────────────────────────────────────────────

    [Fact]
    public void DtapVersionMatches_ReturnsTrue_WhenVersionsAreEqual()
    {
        DeployCommand.DtapVersionMatches("1.2.0.0", "1.2.0.0").Should().BeTrue();
    }

    [Fact]
    public void DtapVersionMatches_ReturnsFalse_WhenGateVersionIsNewerThanPredecessor()
    {
        // Forward skip: promoting a version the predecessor tier hasn't seen yet.
        DeployCommand.DtapVersionMatches("1.0.0.0", "2.0.0.0").Should().BeFalse();
    }

    [Fact]
    public void DtapVersionMatches_ReturnsFalse_WhenGateVersionIsOlderThanPredecessor()
    {
        // Downgrade: the predecessor tier already verified a newer version than this one.
        // Deliberate hotfix-style downgrades aren't a supported flow yet — --skip-dtap-check is the override.
        DeployCommand.DtapVersionMatches("2.0.0.0", "1.0.0.0").Should().BeFalse();
    }

    [Fact]
    public void DtapVersionMatches_ReturnsFalse_WhenPredecessorVersionIsNull()
    {
        DeployCommand.DtapVersionMatches(null, "1.0.0.0").Should().BeFalse();
    }

    [Fact]
    public void DtapVersionMatches_ReturnsFalse_WhenPredecessorVersionIsUnparseable()
    {
        DeployCommand.DtapVersionMatches("not-a-version", "1.0.0.0").Should().BeFalse();
    }

    // ── --path artifact managed-flag validation ──────────────────────────────

    [Fact]
    public void ValidateArtifactManagedFlag_DoesNotThrow_WhenFlagsMatch()
    {
        var act = () => DeployCommand.ValidateArtifactManagedFlag(artifactManaged: true, solutionIncludeManaged: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateArtifactManagedFlag_Throws_WhenArtifactManagedButSolutionUnmanaged()
    {
        var act = () => DeployCommand.ValidateArtifactManagedFlag(artifactManaged: true, solutionIncludeManaged: false);
        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ValidateArtifactManagedFlag_Throws_WhenArtifactUnmanagedButSolutionManaged()
    {
        var act = () => DeployCommand.ValidateArtifactManagedFlag(artifactManaged: false, solutionIncludeManaged: true);
        act.Should().Throw<FlowlineException>();
    }

    private sealed class TempPackageFolder : IDisposable
    {
        public string PackageFolderPath { get; }

        public TempPackageFolder(string solutionXmlContent)
        {
            PackageFolderPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var otherDir = Path.Combine(PackageFolderPath, "src", "Other");
            Directory.CreateDirectory(otherDir);
            File.WriteAllText(Path.Combine(otherDir, "Solution.xml"), solutionXmlContent);
        }

        public void Dispose() => Directory.Delete(PackageFolderPath, recursive: true);
    }
}
