using Flowline;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Console;
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
using System.Text;
using Flowline.Diagnostics;
using ILogger = Serilog.ILogger;

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
    Console.WriteLine("Cancelled.");
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
services.AddSingleton<XrmContextToolProvider>();
services.AddSingleton<XrmContextRunner>();
services.AddSingleton<SecretResolver>();
services.AddSingleton<IGenerator, PacGenerator>();
services.AddSingleton<IGenerator, XrmContext3Generator>();
services.AddSingleton<IGenerator, XrmContextGenerator>();
services.AddSingleton<PluginService>();
services.AddSingleton<WebResourceService>();
services.AddSingleton<FormEventService>();
services.AddSingleton<IPostDeployService, SolutionCheckService>();
services.AddSingleton<IPostDeployService, BackupService>();
OrphanHandlerRegistration.RegisterOrphanHandlers(services);
services.AddSingleton<OrphanCleanupService>();
services.AddSingleton<IPostDeployService>(sp => sp.GetRequiredService<OrphanCleanupService>());
services.AddSingleton<SubprocessCapture>();

Serilog.ILogger? serilogLogger = null;
try
{
    var logPath = FlowlineStoragePaths.GetLogsPath(runTime, args.FirstOrDefault());
    try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { } // Intentional: dir creation failure must not block launch (R16).
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
catch { } // Intentional: Serilog init failure must not block command launch (R16).
services.AddLogging(b => b.ClearProviders().AddSerilog(serilogLogger));

runtimeOptions.ArgsRedacted = SubprocessCapture.RedactSensitiveArgs(string.Join(" ", args));

// Configure and run the app
var app = new CommandApp(new TypeRegistrar(services));
var logLinkShown = false;

app.Configure(config =>
{
    config.SetApplicationName("flowline");
    config.SetApplicationVersion(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "1.0.0");
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

    // clone = Clone solution from environment to local folder
    config.AddCommand<CloneCommand>("clone") // init (new repo) or clone (existing repo)
          .WithDescription("Initialize a Flowline project from an existing Dataverse solution. Creates folder structure, unpacks solution XML, scaffolds Plugins and WebResources projects, and generates AGENTS.md. One-time setup per solution — safe to re-run (will recreate what is missing).")
          .WithExample("clone", "ContosoCustomizations --prod https://contoso.crm4.dynamics.com")
          .WithExample("clone", "ContosoCustomizations --dev https://contoso-test.crm4.dynamics.com --managed")
          .WithExample("clone", "ContosoCustomizations", "--sln");

    // Push assets to dev environment (upload and push assets to environment: plugins, webresources, pcf controls, etc.)
    config.AddCommand<PushCommand>("push")
        .WithDescription("Build and register plugin assembly and web resources directly to DEV — skips pack/import. Reads [[Step]] attributes to create or update plugin registrations. Run after plugin or web resource changes.")
        .WithExample("push")
        .WithExample("push", "ContosoCustomizations --scope webresources")
        .WithExample("push", "ContosoCustomizations --pluginFile ./bin/Release/Plugins.dll --webresources ./dist");

    // Sync changes to local repo (export solution and unpack)
    config.AddCommand<SyncCommand>("sync")
          .WithDescription("Export solution from DEV, bump build version, and unpack to source-controlled XML. Run after testing changes in DEV. Requires no uncommitted changes in Package/src/.")
          .WithExample("sync")
          .WithExample("sync", "--managed", "--bump", "minor");

    // Deploy (pack and import solution into environment)
    config.AddCommand<DeployCommand>("deploy")
          .WithDescription("Pack solution from repo and import into target environment (test, uat, prod, or URL). Requires clean git working directory.")
          .WithExample("deploy", "prod")
          .WithExample("deploy", "test")
          .WithExample("deploy", "https://contoso-test.crm4.dynamics.com/");

    // copy/provision = Copy Source environment to destination environment
    config.AddCommand<ProvisionCommand>("provision")
          .WithDescription("Create a DEV, TEST, or UAT environment by copying from production. Saves environment URL to .flowline. One-time setup for new environments.")
          .WithExample("provision", "dev")
          .WithExample("provision", "dev --prod https://contoso.crm4.dynamics.com  --allow-overwrite")
          .WithExample("provision", "test --copy full --suffix mytest");

    // Generate early-bound C# types from solution entities via pac modelbuilder build
    config.AddCommand<GenerateCommand>("generate")
          .WithDescription("Generate early-bound C# types from solution entities and custom APIs. Overwrites Plugins/Models/ with generated .cs files. Run after adding or modifying entities or custom APIs.")
          .WithExample("generate")
          .WithExample("generate", "ContosoCustomizations --namespace Contoso.Plugins.Models --extra-tables account,contact")
          .WithExample("generate", "--generator", "xrmcontext3");

    config.AddCommand<StatusCommand>("status")
          .WithDescription("Show configured environments, connection status, solution version, PAC CLI auth status, and git state. Use to verify setup before running commands.")
          .WithExample("status");

    // drift = read-only comparison of committed source vs a named live environment (never mutates)
    config.AddCommand<DriftCommand>("drift")
          .WithDescription("Compare committed source against a live environment (dev, test, uat, prod, or a URL) and report components present there but not declared in source. Read-only — never deletes or modifies anything. Run against prod/test for drift detection, or dev before sync/deploy as a preview.")
          .WithExample("drift", "prod")
          .WithExample("drift", "dev")
          .WithExample("drift", "test")
          .WithExample("drift", "https://contoso-test.crm4.dynamics.com/");

    // sln = the project's .sln/.slnx file. A branch rather than a flat 'sln-add' because the noun is
    // the solution *file*, which collides with "solution" meaning the Dataverse artifact everywhere
    // else in this CLI — 'flowline sln add' reads as a one-word substitution for the 'dotnet sln add'
    // the user just watched fail.
    config.AddBranch("sln", sln =>
    {
        sln.SetDescription("Work with the project's solution file (.sln or .slnx).");

        sln.AddCommand<SlnAddCommand>("add")
           .WithDescription("Add a .cdsproj to an existing solution file. 'dotnet sln add' refuses .cdsproj files and exits 0 while doing it, so this writes the entry directly. Doesn't create a solution file — run 'dotnet new sln' first. Runs standalone: no Flowline project needed.")
           .WithExample("sln", "add", "Package/Package.cdsproj");
    });
});

var hookLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(serilogLogger));
AnsiConsole.Console.Pipeline.Attach(new VerboseFilterHook(runtimeOptions));
AnsiConsole.Console.Pipeline.Attach(new LoggingRenderHook(
    hookLoggerFactory.CreateLogger<LoggingRenderHook>()
));

var exitCode = await app.RunAsync(args, cancellationTokenSource.Token);

// Commands that return a non-zero exit code directly (e.g. build/pack failures) instead of throwing
// a FlowlineException skip SetExceptionHandler entirely, so its "Log: ..." pointer never printed.
if (exitCode != 0 && !logLinkShown)
{
    var logFilePath = FlowlineStoragePaths.GetLogsPath(runTime, args.FirstOrDefault());
    AnsiConsole.MarkupLine($"[dim][link={new Uri(logFilePath).AbsoluteUri}]Log: {Markup.Escape(logFilePath)}[/][/]");
}

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
