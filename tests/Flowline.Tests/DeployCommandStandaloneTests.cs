using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;

namespace Flowline.Tests;

// U3: `deploy <url> --path <zip>` from a bare folder with no .flowline and no git repo.
// Covers the standalone predicate, identity-from-manifest construction, and the mode/identity
// feedback line — all extracted as pure decision helpers per this file's established convention.
public class DeployCommandStandaloneTests
{
    // ── ResolveStandalone ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveStandalone_PathSetAndNoProject_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            DeployCommand.ResolveStandalone("artifact.zip", dir).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveStandalone_PathSetButProjectFound_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ProjectConfig.s_configFileName), "{}");

        try
        {
            DeployCommand.ResolveStandalone("artifact.zip", dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveStandalone_NoPath_ReturnsFalse_RegardlessOfProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            DeployCommand.ResolveStandalone(null, dir).Should().BeFalse();
            DeployCommand.ResolveStandalone("", dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── ResolveStandaloneSolution ───────────────────────────────────────────────

    [Fact]
    public void ResolveStandaloneSolution_ManifestHasUniqueName_CarriesUniqueNameAndManagedFlag()
    {
        var sln = DeployCommand.ResolveStandaloneSolution("artifacts/contoso_unmanaged.zip", ("1.0.0.1", true, "contoso_solution"));

        sln.UniqueName.Should().Be("contoso_solution");
        sln.IncludeManaged.Should().BeTrue();
    }

    [Fact]
    public void ResolveStandaloneSolution_ManifestHasUniqueName_Unmanaged_CarriesFalseManagedFlag()
    {
        var sln = DeployCommand.ResolveStandaloneSolution("artifacts/contoso_unmanaged.zip", ("1.0.0.1", false, "contoso_solution"));

        sln.IncludeManaged.Should().BeFalse();
    }

    // R7/KTD3: standalone is the one place a missing UniqueName is fatal — the shared parser itself
    // never throws for it (see DeployCommandSolutionManifestTests.ParseSolutionManifest_ReturnsNullUniqueName_...).
    [Fact]
    public void ResolveStandaloneSolution_ManifestHasNoUniqueName_ThrowsValidationFailedNamingZipAndElement()
    {
        var act = () => DeployCommand.ResolveStandaloneSolution("artifacts/contoso_unmanaged.zip", ("1.0.0.1", false, null));

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Where(e => e.Message.Contains("artifacts/contoso_unmanaged.zip") && e.Message.Contains("UniqueName"));
    }

    // ── BuildStandaloneIdentityNote (R14) ────────────────────────────────────────
    // Standalone-only per the Goal Capsule's "does not change project-mode behavior" — project mode
    // says nothing new here, so there's no project-mode variant of this note to test.

    [Fact]
    public void BuildStandaloneIdentityNote_NamesArtifactFile_NotFlowlineConfig()
    {
        var note = DeployCommand.BuildStandaloneIdentityNote("contoso_unmanaged.zip");

        note.Should().Contain("contoso_unmanaged.zip");
        note.Should().NotContain(".flowline");
    }

    // ── R4: DTAP gate resolves to Skip against the empty config standalone actually runs with ──────
    // (FlowlineCommand.cs:101 falls back to `new ProjectConfig()` when no .flowline is found, so every
    // config URL is empty and ResolveDtapGate's isProd/isUat/isTest checks all fail closed to Skip.
    // No standalone branch needed in the gate itself — this just proves the existing behavior holds.)

    [Fact]
    public void ResolveDtapGate_Skips_AgainstEmptyConfig_AsInStandalone()
    {
        var result = DeployCommand.ResolveDtapGate(new ProjectConfig(), "https://contoso-uat.crm4.dynamics.com");

        result.Outcome.Should().Be(DeployCommand.DtapGateOutcome.Skip);
    }
}
