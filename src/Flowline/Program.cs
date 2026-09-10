using Flowline;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Deploy;
using Flowline.Core.Services;
using Flowline.Core.FormEvents;
using Flowline.Core.OrphanCleanup;
using Flowline.Core.Plugins;
using Flowline.Core.WebResources;
using Flowline.Generators;
using Flowline.Infrastructure;
using Flowline.Logging;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Flowline.Diagnostics;
using ILogger = Serilog.ILogger;
using Spectre.Console.Cli.Help;

Console.OutputEncoding = Encoding.UTF8;

Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;

using var activityListener = new ActivityListener
{
    ShouldListenTo = s => s.Name == "Flowline.CLI",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
};
ActivitySource.AddActivityListener(activityListener);

// Create a cancellation token source to handle Ctrl+C
var cancellationTokenSource = new CancellationTokenSource();

// Wire up Console.CancelKeyPress to trigger cancellation
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Prevent immediate process termination
    cancellationTokenSource.Cancel();
    Console.WriteLine("Cancelled. Panic button acknowledged.");
};

var runtimeOptions = new FlowlineRuntimeOptions();
var runTime = DateTimeOffset.UtcNow;

// Register services
var services = new ServiceCollection();
services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
services.AddSingleton(runtimeOptions);
services.AddSingleton<DataverseConnector>();
services.AddSingleton<ProfileResolutionService>();
services.AddSingleton<HttpClient>();
services.AddSingleton<NuGetVersionClient>();
services.AddSingleton<XrmContextToolProvider>();
services.AddSingleton<XrmContextRunner>();
services.AddSingleton<SecretResolver>();
services.AddSingleton<IGenerator, PacGenerator>();
services.AddSingleton<IGenerator, XrmContext3Generator>();
services.AddSingleton<IGenerator, XrmContextGenerator>();
services.AddSingleton<IGenerator, EbgGenerator>();
services.AddSingleton<PluginService>();
services.AddSingleton<WebResourceService>();
services.AddSingleton<FormEventService>();
// Single registration site (R13) — DeployCommandPostDeployTests resolves the real provider from this
// same method, so the ordering guarantee can't drift from a hand-written test mirror.
PostDeployServiceRegistration.RegisterPostDeployServices(services);
services.AddSingleton<SubprocessCapture>();
services.AddSingleton<ProjectScaffolder>();
services.AddSingleton<SolutionCreateService>();
services.AddSingleton<CreateEnvironmentResolver>();
services.AddSingleton<EnvironmentTargetResolver>();

Serilog.ILogger? serilogLogger = null;
try
{
    var logPath = FlowlineStoragePaths.GetLogsPath(runTime, args.FirstOrDefault());
    try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { } // Intentional: log dir creation failure must not block launch.
    runtimeOptions.TelemetrySalt = new TelemetrySaltStore().LoadOrCreate();
    serilogLogger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .Enrich.With(new ActivityTraceEnricher())
        .Enrich.With(new UrlScrubEnricher(runtimeOptions.TelemetrySalt))
        .Enrich.With(new EmailScrubEnricher(runtimeOptions.TelemetrySalt))
        .WriteTo.File(logPath, rollingInterval: RollingInterval.Infinite)
        .CreateLogger();
    Log.Logger = serilogLogger;
}
catch { } // Intentional: Serilog init failure must not block command launch.
services.AddLogging(b => b.ClearProviders().AddSerilog(serilogLogger));

runtimeOptions.ArgsRedacted = SubprocessCapture.RedactSensitiveArgs(string.Join(" ", args));

// Configure and run the app
const string applicationName = "flowline";
var app = new CommandApp(new TypeRegistrar(services));
var logLinkShown = false;

app.Configure(config =>
{
    config.SetApplicationName(applicationName);
    config.SetApplicationVersion(FlowlineVersion.Display);
    // Must come after the name/version calls — HelpProvider snapshots settings in its constructor.
    FlowlineHelpProvider.UseFlowlineHeaderColor(config.Settings.HelpProviderStyles!);
    config.SetHelpProvider(new FlowlineHelpProvider(config.Settings));
#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif
    config.SetExceptionHandler((ex, _) =>
    {
        var logFilePath = FlowlineStoragePaths.GetLogsPath(runTime, args.FirstOrDefault());
        var logLink = $"[dim][link={new Uri(logFilePath).AbsoluteUri}]Log: {Markup.Escape(logFilePath)}[/][/]";
        logLinkShown = true;

        switch (ex)
        {
            case FlowlineException fe:
                serilogLogger?.Error(ex, "Command failed");
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(fe.Message)}");
                WriteExceptionContext(fe, serilogLogger);
                AnsiConsole.MarkupLine(logLink);
                return (int)fe.ExitCode;
            // A Dataverse request timeout is an environment condition, not a Flowline bug, so it
            // gets the same clean treatment as a FlowlineException. It has to sit above the
            // OperationCanceledException arm: the HttpClient path throws TaskCanceledException,
            // which would otherwise be reported as a user Ctrl+C and exit 130.
            case var _ when DataverseTimeout.Matches(ex, cancellationTokenSource.IsCancellationRequested):
                serilogLogger?.Error(ex, "Dataverse request timed out");
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(DataverseTimeout.Message)}");
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(DataverseTimeout.NextStep(args.FirstOrDefault()))}[/]");
                AnsiConsole.MarkupLine(logLink);
                return (int)ExitCode.Timeout;
            case OperationCanceledException:
                serilogLogger?.Information("Command cancelled by user");
                return (int)ExitCode.Cancelled;
            // Covers CommandParseException (e.g. "--force" with no value swallowed the next
            // token) and other CommandRuntimeException shapes (e.g. a required positional like
            // deploy's <target> going missing because --force consumed it instead) — both are
            // malformed CLI invocations, not application bugs, so they get the same clean
            // treatment as a FlowlineException rather than a raw internal stack trace.
            case CommandRuntimeException cre:
                serilogLogger?.Error(ex, "Command failed");
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(cre.Message)}");
                WriteExceptionContext(cre, serilogLogger);
                AnsiConsole.MarkupLine(logLink);
                return (int)ExitCode.ValidationFailed;
            default:
                serilogLogger?.Error(ex, "Unhandled exception");
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
                WriteExceptionContext(ex, serilogLogger);
                AnsiConsole.MarkupLine(logLink);
                return 1;
        }
    });

    CliConfiguration.Configure(config);
});

var hookLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(serilogLogger));
AnsiConsole.Console.Pipeline.Attach(new VerboseFilterHook(runtimeOptions));
AnsiConsole.Console.Pipeline.Attach(new LoggingRenderHook(
    hookLoggerFactory.CreateLogger<LoggingRenderHook>()
));

// Tab-level state for a long run: the terminal's own progress indicator while the command works, and
// an outcome marker in the title once it stops. Wrapping RunAsync is the only point that sees every
// path that unwinds — SetExceptionHandler runs *inside* CommandApp, so a handled FlowlineException is
// already an exit code by the time it gets here, and a Spectre interceptor would never observe it.
// Ctrl+C needs nothing extra: the token cancels, RunAsync returns ExitCode.Cancelled, and the wrapper
// reports it after the CancelKeyPress handler above has had its say.
var tabStatus = TerminalTabStatus.Start(AnsiConsole.Console, TerminalTabStatus.LabelFor(args, applicationName));

// Environment.Exit (five call sites in GitUtils/PacUtils/DotNetUtils) terminates without unwinding, so
// the wrapper's finally never runs and the indicator would be left spinning for the rest of the
// session. Finish is idempotent, so this is a no-op after a normal exit.
AppDomain.CurrentDomain.ProcessExit += (_, _) => tabStatus.Finish((int)ExitCode.GeneralError);

// ProcessExit does not fire for a bare console app on SIGTERM — a timeout wrapper or a job
// cancellation would otherwise leave the indicator running. Ctrl+C is not handled here; it already
// unwinds through CancelKeyPress and the wrapper below.
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => tabStatus.Finish((int)ExitCode.Cancelled));

var exitCode = await tabStatus.RunAsync(async () =>
{
    var code = await app.RunAsync(args, cancellationTokenSource.Token);

    // Commands that return a non-zero exit code directly (e.g. build/pack failures) instead of throwing
    // a FlowlineException skip SetExceptionHandler entirely, so its "Log: ..." pointer never printed.
    if (code != 0 && !logLinkShown)
    {
        var logFilePath = FlowlineStoragePaths.GetLogsPath(runTime, args.FirstOrDefault());
        AnsiConsole.MarkupLine($"[dim][link={new Uri(logFilePath).AbsoluteUri}]Log: {Markup.Escape(logFilePath)}[/][/]");
    }

    return code;
});

Log.CloseAndFlush();
hookLoggerFactory.Dispose();
return exitCode;

void WriteExceptionContext(Exception ex, ILogger? logger)
{
    foreach (var key in ex.Data.Keys)
    {
        AnsiConsole.MarkupLine($"[dim]{key}: {ex.Data[key]}[/]");
        logger?.Debug("Context: {Key} = {Value}", key, ex.Data[key]);
    }

    if (ex.HelpLink is not null)
        AnsiConsole.MarkupLine($"[dim][link={ex.HelpLink}]See: {Markup.Escape(ex.HelpLink)}[/][/]");
}

namespace Flowline
{
    // R13: single registration site for the pre-import service ordering guarantee — the missing-component
    // gate must stay first (a seconds-long read-only check), ahead of the solution checker and the
    // environment backup, so a doomed deploy stops before that slower work is spent. Do not reorder these
    // three without reading R13 in docs/plans/2026-08-09-001-feat-deploy-import-preflight-plan.md.
    // DeployCommandPostDeployTests resolves a real ServiceProvider from this method, so the ordering
    // guarantee can't drift from a hand-written test mirror.
    internal static class PostDeployServiceRegistration
    {
        public static void RegisterPostDeployServices(IServiceCollection services)
        {
            services.AddSingleton<IPostDeployService, MissingComponentCheckService>();
            services.AddSingleton<IPostDeployService, SolutionCheckService>();
            services.AddSingleton<IPostDeployService, BackupService>();
            OrphanHandlerRegistration.RegisterOrphanHandlers(services);
            // U4/KTD1: factory, not a constant instance — resolved at container-build time, ahead of
            // RootFolder being set on any command instance, so it walks the CWD itself (same lookup
            // FlowlineCommand<TSettings>.ExecuteAsync does for RootFolder) rather than reading a value
            // that doesn't exist yet. No project root found (e.g. a bare CWD) still registers a lookup —
            // anchored there — rather than leaving orphan cleanup unable to run without a repository.
            services.AddSingleton<IComponentProvenanceLookup>(_ =>
                new GitComponentProvenanceLookup(
                    FlowlineCommand<DriftCommand.Settings>.FindFlowlineProjectRoot(Directory.GetCurrentDirectory())
                        ?? Directory.GetCurrentDirectory()));
            services.AddSingleton<OrphanCleanupService>();
            services.AddSingleton<IPostDeployService>(sp => sp.GetRequiredService<OrphanCleanupService>());
            // Pre-import only, and deliberately after the backup above: it is the first service that
            // writes to the target, so a restore point exists before it does. After orphan cleanup too,
            // so cleanup classifies the target it has always seen rather than one holding a record
            // created seconds earlier. Has no skip flag, like the check below.
            services.AddSingleton<IPostDeployService, PluginPackageAssemblyRepairService>();
            // KTD3: last, so it observes the state the deploy actually leaves behind — orphan cleanup
            // above can delete a pluginassembly or redirect to a pluginpackage delete, and a verdict
            // read before that would describe a target that no longer exists. Has no skip flag (R8),
            // so ResolveActiveServices never filters it out.
            services.AddSingleton<IPostDeployService, PluginPackageAssemblyCheckService>();
        }
    }

    // U11/R10/R21/R23/R24: command registration, root examples and strict parsing all live here —
    // ProgramParsingTests builds a CommandApp through this same method, so a test proving the parser's
    // behavior (strict parsing, the pull/sync alias, root examples) proves the real production
    // configuration rather than a hand-written mirror that can drift from it.
    internal static class CliConfiguration
    {
        public static void Configure(IConfigurator config)
        {
            // R10: an old or removed option spelling (e.g. --pluginFile) now fails with the parser's own
            // "Unknown option" error instead of being silently ignored.
            config.Settings.StrictParsing = true;

            // R23: the only root-level examples — clone, push, pull, deploy cover a first run end to
            // end. With none registered, Spectre falls back to filling root help from every child
            // command's own examples instead.
            config.AddExample("clone", "ContosoSales");
            config.AddExample("push");
            config.AddExample("pull");
            config.AddExample("deploy", "prod");

            // init = create a brand-new publisher + empty unmanaged solution in DEV, then scaffold the repo
            config.AddCommand<InitCommand>("init")
                  .WithDescription("Create an empty unmanaged solution and optional a new publisher in a DEV environment, then scaffold the repo around it. Front door for greenfield — no Dataverse solution exists yet. Targets DEV only, via --env dev (default) or a DEV environment URL.")
                  .WithExample("init", "MySolution")
                  .WithExample("init", "MySolution", "--env", "https://contoso-dev.crm4.dynamics.com", "--publisher-prefix", "contoso");

            // clone = Clone solution from environment to local folder
            config.AddCommand<CloneCommand>("clone") // init (new repo) or clone (existing repo)
                  .WithDescription("Initialize a Flowline project from an existing Dataverse solution. Creates folder structure, unpacks solution XML, scaffolds Plugins and WebResources projects, and generates AGENTS.md. Reads from --env <role|url> — usually PROD, the default source of truth — or an already-configured environment when --env is omitted. One-time setup per solution — safe to re-run (will recreate what is missing).")
                  .WithExample("clone", "ContosoCustomizations", "--env", "https://contoso.crm4.dynamics.com")
                  .WithExample("clone", "ContosoCustomizations", "--env", "https://contoso-test.crm4.dynamics.com", "--managed");

            // Push assets to dev environment (upload and push assets to environment: plugins, webresources, pcf controls, etc.)
            config.AddCommand<PushCommand>("push")
                .WithDescription("Build and register plugin assembly and web resources directly to DEV — skips pack/import. Reads [[Step]] attributes to create or update plugin registrations. Run after plugin or web resource changes. Targets DEV only, via --env dev (default) or a DEV environment URL.")
                .WithExample("push")
                .WithExample("push", "ContosoCustomizations", "--scope", "webresources")
                .WithExample("push", "ContosoCustomizations", "--plugin-file", "./bin/Release/Plugins.dll", "--webresources", "./dist");

            // Pull changes to local repo (export solution and unpack). KD9: pull is the primary name,
            // sync a permanent alias — the command class keeps its original name (SyncCommand).
            config.AddCommand<SyncCommand>("pull")
                  .WithDescription("Export solution from DEV, bump the patch version, and unpack to source-controlled XML. Builds afterward to validate the synced source, then writes CHANGES.md and the context docs. Run after testing changes in DEV. Requires no uncommitted changes in the unpacked solution source. Targets DEV only, via --env dev (default) or a DEV environment URL. Alias: sync")
                  .WithAlias("sync")
                  .WithExample("pull")
                  .WithExample("pull", "--managed", "--bump", "minor")
                  .WithExample("pull", "--env", "https://contoso-dev.crm4.dynamics.com");

            // Deploy (pack and import solution into environment)
            config.AddCommand<DeployCommand>("deploy")
                  .WithDescription("Pack solution from repo and import into target environment (test, uat, prod, or URL). Packing from source requires a clean git working directory; deploying a pre-built zip with --path from a folder with no project does not. Reuses a cached pack when nothing changed since the last one; --no-cache forces a fresh pack.")
                  .WithExample("deploy", "prod")
                  .WithExample("deploy", "https://contoso-test.crm4.dynamics.com/")
                  .WithExample("deploy", "test", "--path", "artifacts/ContosoCustomizations_1_2_0_0.zip")
                  .WithExample("deploy", "https://contoso-uat.crm4.dynamics.com/", "--path", "ContosoCustomizations_1_2_0_0.zip");

            // copy/provision = Copy Source environment to destination environment
            config.AddCommand<ProvisionCommand>("provision")
                  .WithDescription("Create a DEV, TEST, or UAT environment by copying from production. Saves environment URL to .flowline. One-time setup for new environments.")
                  .WithExample("provision", "dev")
                  .WithExample("provision", "dev", "--env", "https://contoso.crm4.dynamics.com", "--force", "overwrite")
                  .WithExample("provision", "test", "--copy", "full", "--suffix", "mytest");

            // Generate early-bound C# types from solution entities via pac modelbuilder build
            config.AddCommand<GenerateCommand>("generate")
                  .WithDescription("Generate early-bound C# types from solution entities and custom APIs. Overwrites Plugins/Models/ with generated .cs files. Run after adding or modifying entities or custom APIs. Targets any environment via --env (default: dev).")
                  .WithExample("generate")
                  .WithExample("generate", "ContosoCustomizations", "--namespace", "Contoso.Plugins.Models", "--extra-tables", "account,contact")
                  .WithExample("generate", "--generator", "xrmcontext3");

            config.AddCommand<StatusCommand>("status")
                  .WithDescription("Show configured environments, connection status, solution version, PAC CLI auth status, and git state. Use to verify setup before running commands.")
                  .WithExample("status");

            // drift = read-only comparison of committed source vs a named live environment (never mutates)
            config.AddCommand<DriftCommand>("drift")
                  .WithDescription("Compare committed source against a live environment (dev, test, uat, prod, or a URL) and report components present there but not declared in source. Read-only — never deletes or modifies anything. Run against prod/test for drift detection, or dev before pull/deploy as a preview. Use 'diff' to compare two points in git history instead.")
                  .WithExample("drift", "prod")
                  .WithExample("drift", "test")
                  .WithExample("drift", "https://contoso-test.crm4.dynamics.com/")
                  .WithExample("drift", "https://contoso-test.crm4.dynamics.com/", "--path", "ContosoCustomizations_1_2_0_0.zip");

            // diff = read-only comparison of two points in git history; the git counterpart to drift
            config.AddCommand<DiffCommand>("diff")
                  .WithDescription("Report which solution components changed between two points in git history: two optional ref positionals, or a git-style range. Reads git and the unpacked solution XML only — no Dataverse connection, no authentication, no network. Run before a commit to see what a DEV session actually changed, or between two tags to see what a release contains. Read-only — changes nothing. Use 'drift' to compare against a live environment instead.")
                  .WithExample("diff")
                  .WithExample("diff", "v1.2.0")
                  .WithExample("diff", "v1.2.0", "v1.3.0")
                  .WithExample("diff", "v1.2.0...v1.3.0");

            // settings = the per-environment configuration surface. Sits beside deploy rather than inside it:
            // the values and component states a settings file carries change on their own schedule, and
            // re-running a whole import to flip one flow back on is the wrong unit of work.
            //
            // A branch rather than one leaf with mode flags: five component kinds plus apply and capture do
            // not fit one verb without a flag-contradiction matrix for every pair.
            //
            // Registration order is the grouping (KTD18). Spectre renders commands in the order they are
            // added, not alphabetically, so the two whole-file operations come before the five component
            // kinds and the help reads as two groups with no mechanism. Leaf descriptions are one line each,
            // against the paragraph style the top-level commands carry: a leaf inside a branch is scanned,
            // not discovered.
            config.AddBranch("settings", settings =>
            {
                settings.SetDescription("Read or change one environment's configuration: environment variable values, connection references, and flow, workflow and plugin step state.");

                settings.SetDefaultCommand<SettingsCommand>();

                settings.AddCommand<SettingsPushCommand>("push")
                        .WithDescription("Apply a settings file to an environment. Only components the file names are touched, and re-running the same file changes nothing.")
                        .WithExample("settings", "push", "test")
                        .WithExample("settings", "push", "prod", "--dry-run")
                        .WithExample("settings", "push", "test", "--settings-file", "settings.test.json");

                settings.AddCommand<SettingsPullCommand>("pull")
                        .WithDescription("Write a settings file from an environment. Omit the environment to capture every environment the project configures.")
                        .WithExample("settings", "pull", "test")
                        .WithExample("settings", "pull")
                        .WithExample("settings", "pull", "https://contoso-test.crm4.dynamics.com/", "--solution-name", "ContosoCustomizations");

                // The five component kinds. One class per flag set, not per kind (KTD4): the parser then
                // rejects --value on a flow and --on on a variable with no hand-written check, and the only
                // contradiction left to check by hand is --on together with --off.
                settings.AddCommand<SettingsStateCommand>("flow")
                        .WithDescription("Turn a cloud flow on or off, or read its state.")
                        .WithExample("settings", "flow", "prod", "ApprovalFlow", "--off")
                        .WithExample("settings", "flow", "test", "ApprovalFlow");

                settings.AddCommand<SettingsStateCommand>("workflow")
                        .WithDescription("Turn a classic workflow on or off, or read its state.")
                        .WithExample("settings", "workflow", "prod", "contoso_AutoNumber", "--on");

                settings.AddCommand<SettingsStateCommand>("plugin")
                        .WithDescription("Turn a plugin step on or off, or read its state.")
                        .WithExample("settings", "plugin", "prod", "Contoso.Plugins.AccountPreCreate", "--off");

                settings.AddCommand<SettingsValueCommand>("envvar")
                        .WithDescription("Set an environment variable's value, or read it.")
                        .WithExample("settings", "envvar", "prod", "contoso_ApiUrl", "--value", "https://api.contoso.com")
                        .WithExample("settings", "envvar", "test", "contoso_ApiUrl");

                settings.AddCommand<SettingsValueCommand>("connref")
                        .WithDescription("Bind a connection reference to a connection, or read its binding.")
                        .WithExample("settings", "connref", "prod", "contoso_SharedMailbox", "--value", "a1b2c3d4e5f6");
            });

            // scaffold = write a project template locally; needs no Dataverse connection
            config.AddCommand<ScaffoldCommand>("scaffold")
                  .WithAlias("new")
                  .WithDescription("Write a project template into this folder. Needs no Dataverse connection, no authentication, and no network. Writes the project where you are standing, and looks for a solution file here and upward as far as the repo root: found, the project is named after it and added to it; not found, the template lands alone and the run says so. Skips and changes nothing when the project is already there. Alias: new")
                  .WithExample("scaffold", "webresources")
                  .WithExample("new", "webresources")
                  .WithExample("scaffold", "webresources", "--output", "./ContosoSales", "--name", "Scripts");

            // A branch rather than a flat 'sln-add' because 'flowline sln add' reads as a one-word substitution for the 'dotnet sln add'.
            config.AddBranch("sln", sln =>
            {
                sln.SetDescription(".NET modify solution file (.sln or .slnx) command.");

                sln.AddCommand<SlnAddCommand>("add")
                   .WithDescription("Add a .cdsproj to a solution file. 'dotnet sln add' can't add .cdsproj to a solution file, so this is the replacement for it.")
                   .WithExample("sln", "add", "Solution/MySolution.cdsproj");
            });
        }
    }
}
