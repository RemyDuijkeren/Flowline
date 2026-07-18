using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Models;

namespace Flowline.Tests;

public class StatusCommandTests
{
    [Fact]
    public void ValidateForce_AnyValue_Throws()
    {
        var settings = new StatusCommand.Settings { Force = ["config"] };

        var act = () => StatusCommand.ValidateForce(settings);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Fact]
    public void ValidateForce_Empty_DoesNotThrow()
    {
        var settings = new StatusCommand.Settings();

        var act = () => StatusCommand.ValidateForce(settings);

        act.Should().NotThrow();
    }

    // FormatProfileNote is the report-only decision logic behind status's per-environment profile
    // check (R9): never throws, never prompts, just returns text to print (or null for "nothing to
    // report"). DataverseConnector.FindBestProfile/IsProfileActive aren't mockable (no interface,
    // and the instance overloads read the real machine-wide authprofiles_v2.json with no override
    // seam), so this pure classification step is what's unit-tested here — ExecuteAsync wires it to
    // the real connector but that end-to-end path needs a live pac.exe and isn't covered by this suite.

    [Fact]
    public void FormatProfileNote_ProfileFoundAndActive_ReturnsNull()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", Name = "Dev" };

        var result = StatusCommand.FormatProfileNote(new ProfileFound(profile), isActive: true);

        result.Should().BeNull();
    }

    [Fact]
    public void FormatProfileNote_ProfileFoundButNotActive_ReturnsMismatchNote()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", Name = "Dev" };

        var result = StatusCommand.FormatProfileNote(new ProfileFound(profile), isActive: false);

        result.Should().Contain("mismatch").And.Contain("Dev");
    }

    [Fact]
    public void FormatProfileNote_ProfileFoundNotActive_UnnamedProfile_FallsBackToUser()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", User = "someone@contoso.com" };

        var result = StatusCommand.FormatProfileNote(new ProfileFound(profile), isActive: false);

        result.Should().Contain("someone@contoso.com");
    }

    [Fact]
    public void FormatProfileNote_ProfileAmbiguous_ReturnsCandidateCountNote()
    {
        var candidates = new List<PacProfile>
        {
            new() { Kind = "DATAVERSE", User = "a@contoso.com" },
            new() { Kind = "DATAVERSE", User = "b@contoso.com" },
        };

        var result = StatusCommand.FormatProfileNote(new ProfileAmbiguous(candidates), isActive: false);

        result.Should().Contain("2");
    }

    [Fact]
    public void FormatProfileNote_ProfileNotFound_ReturnsNotFoundNote()
    {
        var result = StatusCommand.FormatProfileNote(new ProfileNotFound("https://contoso.crm.dynamics.com"), isActive: false);

        result.Should().Contain("No local PAC auth profile");
    }

    [Fact]
    public void FormatProfileNote_NeverThrows_ForAnyOutcome()
    {
        var outcomes = new ProfileResolutionResult[]
        {
            new ProfileFound(new PacProfile { Kind = "DATAVERSE" }),
            new ProfileAmbiguous([new PacProfile { Kind = "DATAVERSE" }]),
            new ProfileNotFound("https://contoso.crm.dynamics.com"),
        };

        foreach (var outcome in outcomes)
        {
            var act = () => StatusCommand.FormatProfileNote(outcome, isActive: false);
            act.Should().NotThrow();
        }
    }

    // BuildProfileNotes is what ExecuteAsync actually calls, wired to
    // dataverseConnector.FindBestProfile/IsProfileActive as method groups. DataverseConnector isn't
    // mockable, so these tests inject fakes via the extracted func parameters — covering the plan's
    // listed scenarios (mismatch, ambiguous, not-found, matched-prints-nothing) plus the "never
    // throw" guarantee that the try/catch around FindBestProfile/IsProfileActive exists for.

    static readonly (string Label, string? Url)[] SingleDevEnv = [("Dev", "https://contoso.crm.dynamics.com")];

    [Fact]
    public void BuildProfileNotes_ProfileMatchesActive_NoEntryForEnv()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", Name = "Dev" };

        var notes = StatusCommand.BuildProfileNotes(SingleDevEnv, _ => new ProfileFound(profile), _ => true);

        notes.Should().ContainKey("Dev").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void BuildProfileNotes_ProfileMismatch_RecordsMismatchNote()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", Name = "Dev" };

        var notes = StatusCommand.BuildProfileNotes(SingleDevEnv, _ => new ProfileFound(profile), _ => false);

        notes["Dev"].Should().Contain("mismatch");
    }

    [Fact]
    public void BuildProfileNotes_ProfileAmbiguous_RecordsAmbiguousNote()
    {
        ProfileResolutionResult Resolve(string url) => new ProfileAmbiguous([new PacProfile(), new PacProfile()]);

        var notes = StatusCommand.BuildProfileNotes(SingleDevEnv, Resolve, _ => false);

        notes["Dev"].Should().Contain("2");
    }

    [Fact]
    public void BuildProfileNotes_ProfileNotFound_RecordsNotFoundNote()
    {
        var notes = StatusCommand.BuildProfileNotes(
            SingleDevEnv, url => new ProfileNotFound(url), _ => false);

        notes["Dev"].Should().Contain("No local PAC auth profile");
    }

    [Fact]
    public void BuildProfileNotes_FindBestProfileThrowsFlowlineException_DoesNotThrow_AndSkipsEnv()
    {
        ProfileResolutionResult Resolve(string url) =>
            throw new FlowlineException(ExitCode.NotAuthenticated, "no auth profile file on this machine");

        var act = () => StatusCommand.BuildProfileNotes(SingleDevEnv, Resolve, _ => false);

        var notes = act.Should().NotThrow().Subject;
        notes.Should().NotContainKey("Dev");
    }

    [Fact]
    public void BuildProfileNotes_IsProfileActiveThrowsFlowlineException_DoesNotThrow_AndSkipsEnv()
    {
        var profile = new PacProfile { Kind = "DATAVERSE" };
        bool IsActive(PacProfile p) => throw new FlowlineException(ExitCode.NotAuthenticated, "boom");

        var act = () => StatusCommand.BuildProfileNotes(SingleDevEnv, _ => new ProfileFound(profile), IsActive);

        var notes = act.Should().NotThrow().Subject;
        notes.Should().NotContainKey("Dev");
    }

    [Fact]
    public void BuildProfileNotes_FindBestProfileThrowsNonFlowlineException_DoesNotThrow_AndSkipsEnv()
    {
        ProfileResolutionResult Resolve(string url) =>
            throw new UnauthorizedAccessException("access denied reading authprofiles_v2.json");

        var act = () => StatusCommand.BuildProfileNotes(SingleDevEnv, Resolve, _ => false);

        var notes = act.Should().NotThrow().Subject;
        notes.Should().NotContainKey("Dev");
    }

    [Fact]
    public void BuildProfileNotes_UnconfiguredEnv_IsSkipped_NoConnectorCall()
    {
        (string Label, string? Url)[] envs = [("Dev", null)];
        var called = false;

        StatusCommand.BuildProfileNotes(envs, url => { called = true; return new ProfileNotFound(url); }, _ => false);

        called.Should().BeFalse();
    }

    [Fact]
    public void BuildProfileNotes_MultipleEnvs_EachResolvedIndependently()
    {
        (string Label, string? Url)[] envs =
        [
            ("Dev", "https://dev.crm.dynamics.com"),
            ("Prod", "https://prod.crm.dynamics.com"),
        ];
        var devProfile = new PacProfile { Kind = "DATAVERSE", Name = "Dev" };

        ProfileResolutionResult Resolve(string url) =>
            url.Contains("dev") ? new ProfileFound(devProfile) : new ProfileNotFound(url);

        var notes = StatusCommand.BuildProfileNotes(envs, Resolve, _ => true);

        notes["Dev"].Should().BeNull();
        notes["Prod"].Should().Contain("No local PAC auth profile");
    }
}
