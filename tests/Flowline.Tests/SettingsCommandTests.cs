using Flowline.Settings;
using FluentAssertions;
using Flowline.Core.Environments;
using Flowline.Commands;
using Flowline.Core.Config;
using Flowline.Core;
using Flowline.Core.Configure;

namespace Flowline.Tests;

public class SettingsCommandTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public SettingsCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    void WithProject() => File.WriteAllText(Path.Combine(_dir, ProjectConfig.s_configFileName), "{}");

    // ── The --from artifact seam ─────────────────────────────────────────────

    // This is what decides whether a capture hands `pac` --solution-zip or --solution-folder, and getting it
    // wrong fails inside the subprocess, where the cause is hard to read. It is a pure function, so the
    // branch that needs a real `pac` is the only part these cannot reach.

    [Fact]
    public void ResolveSolutionInput_Folder_IsNotAZip()
    {
        var folder = Path.Combine(_dir, "Solution", "src");
        Directory.CreateDirectory(folder);

        var (path, isZip) = SettingsSupport.ResolveSolutionInput(folder);

        isZip.Should().BeFalse();
        path.Should().Be(Path.GetFullPath(folder));
    }

    [Fact]
    public void ResolveSolutionInput_File_IsAZip()
    {
        var zip = Path.Combine(_dir, "ContosoCustomizations.zip");
        File.WriteAllText(zip, "not really a zip, but it exists");

        var (path, isZip) = SettingsSupport.ResolveSolutionInput(zip);

        isZip.Should().BeTrue();
        path.Should().Be(Path.GetFullPath(zip));
    }

    // pac is given the resolved path, so a relative argument has to survive the temp-directory switch a pull
    // runs inside — resolving it late would look for the artifact in the wrong place.
    //
    // The base directory is passed rather than moved to. This test used to set the process working
    // directory and put it back, which is global state every other test in the assembly shares: xUnit runs
    // classes in parallel, and the ten or so classes here that start a subprocess inherit the working
    // directory at the moment they launch. That made an unrelated test fail roughly one run in ten.
    [Fact]
    public void ResolveSolutionInput_RelativePath_IsMadeAbsolute()
    {
        var folder = Path.Combine(_dir, "Solution");
        Directory.CreateDirectory(folder);

        var (path, _) = SettingsSupport.ResolveSolutionInput("Solution", _dir);

        Path.IsPathRooted(path).Should().BeTrue();
        path.Should().Be(Path.GetFullPath(folder));
    }

    [Fact]
    public void ResolveSolutionInput_NothingAtThePath_ThrowsNotFoundNamingIt()
    {
        var missing = Path.Combine(_dir, "no-such-solution.zip");

        var act = () => SettingsSupport.ResolveSolutionInput(missing);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    // ── Stand-alone resolution (KTD3) ────────────────────────────────────────

    [Fact]
    public void ResolveStandalone_SolutionNameAndNoProject_IsStandalone()
    {
        SettingsSupport.ResolveStandalone("ContosoCustomizations", null, _dir).Should().BeTrue();
    }

    [Fact]
    public void ResolveStandalone_SolutionNameButProjectFound_IsNotStandalone()
    {
        // The project wins: inside a checkout the solution comes from the project file, and a flag that
        // disagreed with it would apply a settings file against a solution the checkout does not describe.
        WithProject();

        SettingsSupport.ResolveStandalone("ContosoCustomizations", null, _dir).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveStandalone_NeitherFlag_IsNotStandalone(string? solutionName)
    {
        SettingsSupport.ResolveStandalone(solutionName, null, _dir).Should().BeFalse();
    }

    // A stand-alone pull names the solution through the artifact rather than through --solution-name. Judged
    // project mode, it would stop at "No Flowline project found" with the artifact it was handed ignored.
    [Fact]
    public void ResolveStandalone_PullArtifactAndNoProject_IsStandalone()
    {
        SettingsSupport.ResolveStandalone(null, "ContosoCustomizations.zip", _dir).Should().BeTrue();
    }

    [Fact]
    public void ResolveStandalone_PullArtifactButProjectFound_IsNotStandalone()
    {
        WithProject();

        SettingsSupport.ResolveStandalone(null, "ContosoCustomizations.zip", _dir).Should().BeFalse();
    }

    // A bare --pull inside no project still is not stand-alone: the flag carries no value, so nothing names
    // the solution.
    [Fact]
    public void ResolveStandalone_BarePullWithNoValue_IsNotStandalone()
    {
        SettingsSupport.ResolveStandalone(null, null, _dir).Should().BeFalse();
    }

    // ── Flag validation ──────────────────────────────────────────────────────

    // Inside a project --solution-name follows push and generate: the project's own name is accepted, a
    // different one is rejected naming both, and leaving it out is always fine.

    [Fact]
    public void ValidateSolutionName_MatchingNameInsideAProject_IsAccepted()
    {
        var act = () => SettingsSupport.ValidateSolutionName("contosocustomizations", "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionName_DifferentNameInsideAProject_IsRejected()
    {
        var act = () => SettingsSupport.ValidateSolutionName("OtherSolution", "ContosoCustomizations");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .WithMessage("*OtherSolution*ContosoCustomizations*");
    }

    [Fact]
    public void ValidateSolutionName_NoNameInsideAProject_IsAccepted()
    {
        var act = () => SettingsSupport.ValidateSolutionName(null, "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionName_ProjectWithoutASolution_IsAccepted()
    {
        var act = () => SettingsSupport.ValidateSolutionName("ContosoCustomizations", null);

        act.Should().NotThrow();
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    // R11: the run says which file it read and which environment it is about to write, before it writes.
    // There is no confirmation prompt (KTD10), so this line is the only wrong-file-wrong-environment guard
    // an operator gets.
    [Fact]
    public void BuildResolutionNote_NamesTheFileAndTheEnvironment()
    {
        var location = new SettingsFileLocation(
            Path.Combine("C:", "repo", "Solution", "settings.test.json"), SettingsFileSource.RoleConvention, Exists: true);

        var note = SettingsSupport.BuildResolutionNote(location);

        note.Should().Contain("settings.test.json");
    }

    [Theory]
    [InlineData(SettingsFileSource.Explicit, "--settings-file")]
    [InlineData(SettingsFileSource.RoleConvention, "role convention")]
    [InlineData(SettingsFileSource.SharedFallback, "shared fallback")]
    public void BuildResolutionNote_SaysWhyThatFileWasChosen(SettingsFileSource source, string expected)
    {
        var location = new SettingsFileLocation("settings.json", source, Exists: true);

        SettingsSupport.BuildResolutionNote(location).Should().Contain(expected);
    }

    [Fact]
    public void BuildDryRunCompleteMessage_SaysNothingWasWrittenAndHowToApply()
    {
        var message = SettingsSupport.BuildDryRunCompleteMessage("Contoso PROD");

        message.Should().Contain("Contoso PROD");
        message.Should().Contain("untouched");
        message.Should().Contain("--dry-run");
    }

    // Rule 5 of the tone guide: one finish line, always last. A real apply used to end on the summary
    // count and never print one, so a successful run just stopped.
    [Fact]
    public void BuildAppliedMessage_NamesTheEnvironmentAndSaysReRunningIsSafe()
    {
        var message = SettingsSupport.BuildAppliedMessage("Contoso TEST");

        message.Should().Contain("Contoso TEST");
        message.Should().Contain("Re-run");
    }

    // An interrupted run must not sign off with the success line. It has written to a live environment and
    // stopped partway, so the finish line has to say both halves and point at the re-run.
    [Fact]
    public void BuildCancelledMessage_SaysWhatLandedAndWhatDidnt()
    {
        var message = SettingsSupport.BuildCancelledMessage("Contoso TEST");

        message.Should().Contain("Contoso TEST");
        message.Should().Contain("the rest weren't");
        message.Should().Contain("Re-run");
        message.Should().NotBe(SettingsSupport.BuildAppliedMessage("Contoso TEST"));
    }

    [Fact]
    public void BuildPullDryRunMessage_SaysTheFileWasntWritten()
    {
        // A pull's dry run leaves a file unwritten; saying the environment is untouched would describe the
        // wrong thing entirely, since a pull never writes to one.
        var message = SettingsSupport.BuildPullDryRunMessage("Solution/deploymentSettings.test.json");

        message.Should().Contain("deploymentSettings.test.json");
        message.Should().Contain("wasn't written");
    }

    [Theory]
    [InlineData(1, "1 component isn't recorded in the settings file")]
    [InlineData(46, "46 components aren't recorded in the settings file")]
    public void BuildUndeclaredWarning_ReadsAsAColleagueWroteIt(int count, string expected)
    {
        SettingsSupport.BuildUndeclaredWarning(count).Should().StartWith(expected);
    }

    // The warning has to say what it costs, not how many lines are missing. It used to report that N
    // components "aren't in the file", which is true of most of any solution and so was never a reason to
    // act -- a warning that fires unchanged on every run is one people learn to skip.
    [Fact]
    public void BuildUndeclaredWarning_SaysWhatIsAtStakeAndHowToFixIt()
    {
        var message = SettingsSupport.BuildUndeclaredWarning(3);

        message.Should().Contain("a fresh deploy wouldn't reproduce");

        // The full invocation, not the bare operation: a suggestion an agent can paste has to include
        // the executable.
        message.Should().Contain("flowline settings capture");
    }

    // ── Force vocabulary (KTD10) ─────────────────────────────────────────────

    // configure writes only what the file declares, so it has no destructive scope to gate. Pinned so a
    // later change that adds a confirmation has to break this test first — every CI job already running
    // `configure prod` unattended would stop working.
    [Fact]
    public void ForceVocabulary_CarriesNoConfigureSpecificSpecifier()
    {
        FlowlineSettings.ConfigOnlyValidSpecifiers.Should().BeEquivalentTo(["config", "all"]);
    }

    // ── Capture destination ──────────────────────────────────────────────────

    // KTD17: the one thing a capture must never do is fall back to the shared, un-suffixed file. That file
    // is a live fallback for every environment, so one environment's connection ids written there would be
    // applied everywhere.
    [Fact]
    public void BuildUninferrableRoleError_NamesTheTargetAndTheFlagThatFixesIt()
    {
        var error = SettingsSupport.BuildUninferrableRoleError("https://contoso.crm4.dynamics.com");

        error.Should().Contain("contoso.crm4.dynamics.com");
        error.Should().Contain("--settings-file");
    }

    [Fact]
    public void BuildSweepDestinationError_SaysWhyOnePathCannotServeASweep()
    {
        var error = SettingsSupport.BuildSweepDestinationError();

        error.Should().Contain("--settings-file");
        error.Should().Contain("every configured environment");
    }

    // ── Stand-alone detection after the flag rename ──────────────────────────

    // The artifact flag is --from now, not --pull, but the rule it feeds is unchanged: a flag naming the
    // solution plus no project to read one from.
    [Fact]
    public void ResolveStandalone_ArtifactFlagOutsideAProject_IsStandalone()
    {
        SettingsSupport.ResolveStandalone(null, "artifacts/ContosoCustomizations.zip", _dir).Should().BeTrue();
    }

    // ── The sweep ────────────────────────────────────────────────────────────

    // The bug this pins: the sweep passed each role's .flowline config key ("DevUrl") where the role
    // parser only accepts a keyword ("dev"). Every role resolved to nothing and was treated as a URL, so
    // every environment in the sweep failed. Nothing in the type system objects to either string.
    [Theory]
    [InlineData(EnvironmentRole.Prod)]
    [InlineData(EnvironmentRole.Uat)]
    [InlineData(EnvironmentRole.Test)]
    [InlineData(EnvironmentRole.Dev)]
    public void EveryRoleKeyword_RoundTripsBackToItsRole(EnvironmentRole role) =>
        EnvironmentRoles.TryParse(role.Keyword()).Should().Be(role);

    [Theory]
    [InlineData(EnvironmentRole.Prod)]
    [InlineData(EnvironmentRole.Dev)]
    public void ARolesConfigKey_IsNotAKeywordTheParserAccepts(EnvironmentRole role) =>
        EnvironmentRoles.TryParse(role.ConfigKey()).Should().BeNull();

    // One unreachable environment must not cost the captures that would have succeeded — the whole
    // reason the sweep captures each role on its own.
    [Fact]
    public async Task ASweep_KeepsGoingAfterOneEnvironmentFails()
    {
        var attempted = new List<EnvironmentRole>();
        var reported = new List<EnvironmentRole>();

        var failed = await SettingsCaptureCommand.CaptureEachAsync(
            [EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Prod],
            role =>
            {
                attempted.Add(role);
                return role == EnvironmentRole.Test
                    ? throw new InvalidOperationException("unreachable")
                    : Task.CompletedTask;
            },
            (role, _) => reported.Add(role),
            CancellationToken.None);

        attempted.Should().Equal(EnvironmentRole.Dev, EnvironmentRole.Test, EnvironmentRole.Prod);
        failed.Should().Equal(EnvironmentRole.Test);
        reported.Should().Equal(EnvironmentRole.Test);
    }

    [Fact]
    public async Task ASweep_WhereEveryEnvironmentAnswers_ReportsNoFailures()
    {
        var failed = await SettingsCaptureCommand.CaptureEachAsync(
            [EnvironmentRole.Dev, EnvironmentRole.Prod],
            _ => Task.CompletedTask,
            (_, _) => throw new Xunit.Sdk.XunitException("nothing failed, so nothing should be reported"),
            CancellationToken.None);

        failed.Should().BeEmpty();
    }

    // Cancellation stops the run. Reporting the roles it never reached as unreachable would describe
    // something that was never attempted.
    [Fact]
    public async Task ASweep_TreatsCancellationAsStoppingRatherThanAsAFailedEnvironment()
    {
        var reported = new List<EnvironmentRole>();

        var act = async () => await SettingsCaptureCommand.CaptureEachAsync(
            [EnvironmentRole.Dev, EnvironmentRole.Test],
            _ => throw new OperationCanceledException(),
            (role, _) => reported.Add(role),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        reported.Should().BeEmpty();
    }

    // ── Shared report wording ────────────────────────────────────────────────

    [Fact]
    public void BuildWouldChangeLine_NamesTheComponentAndPromisesNothingYet()
    {
        var line = SettingsSupport.BuildWouldChangeLine("ApprovalFlow");

        line.Should().Contain("ApprovalFlow");
        line.Should().StartWith("Would change");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void BuildUpdatedLine_SaysWhetherItHadSuspendedItself(bool wasSuspended, bool expectMention)
    {
        var line = SettingsSupport.BuildUpdatedLine("ApprovalFlow", wasSuspended);

        line.Should().Contain("ApprovalFlow").And.Contain("updated");
        line.Contains("suspended").Should().Be(expectMention);
    }

    // Markup in a component name would otherwise be parsed as console styling.
    [Fact]
    public void BuildUpdatedLine_EscapesAComponentNameThatLooksLikeMarkup() =>
        SettingsSupport.BuildUpdatedLine("Flow [red]x[/]", wasSuspended: false)
            .Should().Contain("[[red]]");

    // A component outcome that carried no detail used to report "<name> failed", which names the
    // component and nothing else. An agent reading that cannot tell whether to retry, fix something, or
    // stop, so both fallbacks have to name the component and the next move.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADetaillessOutcome_NamesTheComponentAndWhatToDo(bool refused)
    {
        var line = refused
            ? SettingsSupport.BuildRefusedLine("ApprovalFlow")
            : SettingsSupport.BuildFailedLine("ApprovalFlow");

        line.Should().Contain("ApprovalFlow");
        line.Should().Contain("maker portal");
    }

    // Retry is the right first move for a failure and the wrong one for a refusal: Dataverse turning the
    // change down does not become true on the second attempt.
    [Fact]
    public void OnlyAFailure_AsksForARetry()
    {
        SettingsSupport.BuildFailedLine("ApprovalFlow").Should().Contain("Re-run");
        SettingsSupport.BuildRefusedLine("ApprovalFlow").Should().NotContain("Re-run to retry");
    }

    // ── KTD23: the transposed invocation ─────────────────────────────────────

    // "In dev, do flow" is how the task is described out loud, so this is what fingers reach for.
    // Spectre answers "Unknown command 'dev'", which names the token it could not route without saying
    // the token is fine and only in the wrong place.
    [Fact]
    public void AnEnvironmentBeforeTheOperation_IsAnsweredWithTheLineToRun()
    {
        var hint = SettingsSupport.BuildArgumentOrderHint(["settings", "dev", "toggle"]);

        hint.Should().Contain("flowline settings toggle dev");
    }

    // The suggestion has to be runnable, so the rest of the invocation comes with it and a component
    // name keeps its quotes.
    [Fact]
    public void TheSuggestion_CarriesTheRestOfTheInvocation()
    {
        var hint = SettingsSupport.BuildArgumentOrderHint(
            ["settings", "prod", "toggle", "Nightly reconciliation", "--off"]);

        hint.Should().Contain("flowline settings toggle prod \"Nightly reconciliation\" --off");
    }

    // A URL is as valid a target as a role keyword, and is the case a role-only check would miss.
    [Fact]
    public void AUrlBeforeTheOperation_IsRecognisedToo()
    {
        SettingsSupport.BuildArgumentOrderHint(["settings", "https://contoso.crm4.dynamics.com", "set"])
            .Should().Contain("flowline settings set https://contoso.crm4.dynamics.com");
    }

    // No operation anywhere: say where it belongs and what the operations are.
    [Fact]
    public void AnEnvironmentWithNoOperation_NamesTheOperations()
    {
        var hint = SettingsSupport.BuildArgumentOrderHint(["settings", "dev"]);

        hint.Should().Contain("is an environment, not an operation");
        hint.Should().Contain("apply").And.Contain("capture").And.Contain("toggle").And.Contain("set");
    }

    // Wrong twice over: the environment first and a retired name second. The retired name is the more
    // useful thing to explain, because fixing only the order would still not run.
    [Fact]
    public void ARetiredNameAfterTheEnvironment_IsAnsweredWithBothCorrections()
    {
        var hint = SettingsSupport.BuildArgumentOrderHint(
            ["settings", "prod", "flow", "Nightly reconciliation", "--off"]);

        hint.Should().Contain("flowline settings toggle prod \"Nightly reconciliation\" --off --type flow");
    }

    // A mistyped operation is not this mistake. Spectre's own "unknown command" serves that better than
    // a confident guess about what was meant.
    [Theory]
    [InlineData("flwo")]
    [InlineData("something-else")]
    public void AMistypedOperation_IsLeftToTheParser(string token)
    {
        SettingsSupport.BuildArgumentOrderHint(["settings", token]).Should().BeNull();
    }

    // A well-formed invocation must never be second-guessed, whatever else the parser objected to.
    [Fact]
    public void AnOperationInTheRightPlace_GetsNoHint()
    {
        SettingsSupport.BuildArgumentOrderHint(["settings", "toggle", "dev"]).Should().BeNull();
        SettingsSupport.BuildArgumentOrderHint(["settings", "set", "dev"]).Should().BeNull();
        SettingsSupport.BuildArgumentOrderHint(["settings", "apply", "prod"]).Should().BeNull();
    }

    [Fact]
    public void AnotherCommandEntirely_GetsNoHint()
    {
        SettingsSupport.BuildArgumentOrderHint(["deploy", "prod"]).Should().BeNull();
        SettingsSupport.BuildArgumentOrderHint([]).Should().BeNull();
    }

    // Every retired name the hint knows maps to an operation and a type the parser accepts, so the line
    // it suggests is one that runs.
    [Theory]
    [InlineData("flow", "toggle", SettingsToggleCommand.StateType.Flow)]
    [InlineData("workflow", "toggle", SettingsToggleCommand.StateType.Workflow)]
    [InlineData("plugin", "toggle", SettingsToggleCommand.StateType.Plugin)]
    public void EveryRetiredStateName_SuggestsATypeTheParserAccepts(
        string retired, string operation, SettingsToggleCommand.StateType type)
    {
        SettingsSupport.BuildArgumentOrderHint(["settings", retired, "dev"])
            .Should().Contain($"flowline settings {operation} dev --type {retired}");

        var routes = () => SettingsToggleCommand.KindFor(type);
        routes.Should().NotThrow();
    }

    [Theory]
    [InlineData("envvar", SettingsSetCommand.ValueType.EnvVar)]
    [InlineData("connref", SettingsSetCommand.ValueType.ConnRef)]
    public void EveryRetiredValueName_SuggestsATypeTheParserAccepts(
        string retired, SettingsSetCommand.ValueType type)
    {
        SettingsSupport.BuildArgumentOrderHint(["settings", retired, "dev"])
            .Should().Contain($"flowline settings set dev --type {retired}");

        var routes = () => SettingsSetCommand.KindFor(type);
        routes.Should().NotThrow();
    }

    // ── A target that is not an environment ──────────────────────────────────

    [Fact]
    public void ASwappedOrder_IsAnsweredWithTheLineToRun()
    {
        var message = SettingsSupport.BuildNotAnEnvironmentError(
            "ApprovalFlow", "prod", ["settings", "toggle", "ApprovalFlow", "prod", "--off"]);

        message.Should().Contain("environment comes first")
            .And.Contain("'flowline settings toggle prod ApprovalFlow --off'");
    }

    [Fact]
    public void ASwappedUrl_GoesFirstInTheSuggestion()
    {
        var message = SettingsSupport.BuildNotAnEnvironmentError(
            "contoso_ApiUrl", "https://contoso.crm4.dynamics.com/",
            ["settings", "set", "contoso_ApiUrl", "https://contoso.crm4.dynamics.com/"]);

        message.Should().Contain("'flowline settings set https://contoso.crm4.dynamics.com/ contoso_ApiUrl'");
    }

    // An option before the positionals is legal, so the suggestion cannot assume where they sit.
    [Fact]
    public void ASwappedOrderWithAnOptionFirst_SwapsThePositionalsNotTheOption()
    {
        var message = SettingsSupport.BuildNotAnEnvironmentError(
            "ApprovalFlow", "prod", ["settings", "toggle", "--type", "flow", "ApprovalFlow", "prod", "--off"]);

        message.Should().Contain("'flowline settings toggle --type flow prod ApprovalFlow --off'");
    }

    // A value can be a secret, and this line lands in CI logs and the log file.
    [Theory]
    [InlineData("--value", "secret123")]
    [InlineData("--value=secret123", null)]
    public void ASwappedSet_NeverRepeatsTheValue(string option, string? value)
    {
        string[] args = value is null
            ? ["settings", "set", "contoso_Key", "prod", option]
            : ["settings", "set", "contoso_Key", "prod", option, value];

        var message = SettingsSupport.BuildNotAnEnvironmentError("contoso_Key", "prod", args);

        message.Should().NotContain("secret123").And.Contain("<value>");
    }

    [Fact]
    public void ASwappedNameWithSpaces_StaysQuoted()
    {
        var message = SettingsSupport.BuildNotAnEnvironmentError(
            "Nightly reconciliation", "prod", ["settings", "toggle", "Nightly reconciliation", "prod", "--off"]);

        message.Should().Contain("'flowline settings toggle prod \"Nightly reconciliation\" --off'");
    }

    // Nothing to swap with: the second word is not an environment either.
    [Theory]
    [InlineData("Nightly reconciliation")]
    [InlineData(null)]
    public void ABadTargetWithNoEnvironmentToSwap_GetsTheGenericMessage(string? name)
    {
        var message = SettingsSupport.BuildNotAnEnvironmentError(
            "ApprovalFlow", name, ["settings", "toggle", "ApprovalFlow", "--off"]);

        message.Should().StartWith("'ApprovalFlow' isn't a role (dev, test, uat, prod) or a URL.")
            .And.Contain("flowline settings <operation> <dev|test|uat|prod|url>")
            .And.NotContain("comes first");
    }

    // ── A scripted write with no name ────────────────────────────────────────

    // A script that passed --off with no name used to get the component list and exit 0, so it believed
    // something was turned off and nothing was.
    [Fact]
    public void AWriteWithNoName_IsAnsweredWithTheCallersLineAndAName()
    {
        var message = SettingsSupport.BuildMissingNameError(
            "flow", "turn off", "prod", ["settings", "toggle", "prod", "--off"]);

        message.Should().Be("Name the flow to turn off: 'flowline settings toggle prod <name> --off'.");
    }

    // The suggestion keeps every flag, so pasting it after filling in the name still previews rather
    // than writes when the caller asked for a dry run.
    [Fact]
    public void TheSuggestion_KeepsTheCallersFlags_WhereverTheyAre()
    {
        var message = SettingsSupport.BuildMissingNameError(
            "flow", "turn on", "prod", ["settings", "toggle", "--type", "flow", "prod", "--on", "--dry-run"]);

        message.Should().Contain("'flowline settings toggle --type flow prod <name> --on --dry-run'");
    }

    [Theory]
    [InlineData(new[] { "settings", "set", "prod", "--value", "https://api.contoso.com" }, "--value <value>")]
    [InlineData(new[] { "settings", "set", "prod", "--value=https://api.contoso.com" }, "--value=<value>")]
    public void TheSuggestion_NeverRepeatsTheValue(string[] args, string expected)
    {
        var message = SettingsSupport.BuildMissingNameError("component", "set", "prod", args);

        message.Should().Contain($"prod <name> {expected}").And.NotContain("api.contoso.com");
    }

    // ── An unknown --type ────────────────────────────────────────────────────

    // The parser answers with "Failed to convert 'bogus' to Nullable`1", which leaks a CLR type name and
    // capitalises values the user types in lower case.
    [Theory]
    [InlineData("toggle", "flow, workflow, rule, bpf, action, plugin")]
    [InlineData("set", "envvar, connref")]
    public void AnUnknownType_IsAnsweredWithTheValidOnes(string operation, string expected)
    {
        var hint = SettingsSupport.BuildTypeValueHint(["settings", operation, "dev", "--type", "bogus"]);

        hint.Should().Contain($"'bogus' isn't a type for 'settings {operation}'");
        hint.Should().Contain(expected);
    }

    [Fact]
    public void AnUnknownType_IsRecognisedInTheEqualsSpellingToo()
    {
        SettingsSupport.BuildTypeValueHint(["settings", "toggle", "dev", "--type=bogus"])
            .Should().Contain("bogus");
    }

    // A type the parser does accept is not this mistake, whatever else went wrong, and neither is an
    // operation that has no --type at all.
    [Theory]
    [InlineData("settings|toggle|dev|--type|flow")]
    [InlineData("settings|toggle|dev|--type|PLUGIN")]
    [InlineData("settings|set|dev|--type|connref")]
    [InlineData("settings|pull|dev|--type|flow")]
    [InlineData("settings|toggle|dev")]
    public void AValidOrIrrelevantType_GetsNoHint(string invocation)
    {
        SettingsSupport.BuildTypeValueHint(invocation.Split('|')).Should().BeNull();
    }
}
