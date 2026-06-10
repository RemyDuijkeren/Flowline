using Flowline;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

// Create a cancellation token source to handle Ctrl+C
var cancellationTokenSource = new CancellationTokenSource();

// Wire up Console.CancelKeyPress to trigger cancellation
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Prevent immediate process termination
    cancellationTokenSource.Cancel();
    Console.WriteLine("Cancelled.");
};

// Register services
var services = new ServiceCollection();
services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
services.AddSingleton<FlowlineRuntimeOptions>();
services.AddSingleton<DataverseConnector>();
services.AddSingleton<PluginService>();
services.AddSingleton<WebResourceService>();
services.AddSingleton<OrphanCleanupService>();

// Configure and run the app
var app = new CommandApp(new TypeRegistrar(services));

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
        switch (ex)
        {
            case FlowlineException fe:
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(fe.Message)}");
                fe.Detail?.Invoke(AnsiConsole.Console);
                if (fe.HelpLink is not null)
                    AnsiConsole.MarkupLine($"[dim]See: {fe.HelpLink}[/]");
                return (int)fe.ExitCode;
            case OperationCanceledException:
                return (int)ExitCode.Cancelled;
            default:
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
                return 1;
        }
    });

    // clone = Clone solution from environment to local folder
    config.AddCommand<CloneCommand>("clone") // init (new repo) or clone (existing repo)
          .WithDescription("Initialize a Flowline project from an existing Dataverse solution. Creates folder structure, unpacks solution XML, scaffolds Plugins and WebResources projects, and generates AGENTS.md. One-time setup per solution.")
          .WithExample("clone", "ContosoCustomizations --prod https://contoso.crm4.dynamics.com")
          .WithExample("clone", "ContosoCustomizations --test https://contoso-test.crm4.dynamics.com --managed")
          .WithExample("clone", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com");

    // copy/provision = Copy Source environment to destination environment
    config.AddCommand<ProvisionCommand>("provision")
          .WithDescription("Create a DEV, TEST, or UAT environment by copying from production. Saves environment URL to .flowline. One-time setup for new environments.")
          .WithExample("provision")
          .WithExample("provision", "dev")
          .WithExample("provision", "test")
          .WithExample("provision", "dev --prod https://contoso.crm4.dynamics.com")
          .WithExample("provision", "dev --copy full")
          .WithExample("provision", "dev --suffix mydev")
          .WithExample("provision", "dev --allow-overwrite");

    // Push assets to dev environment (upload and push assets to environment: plugins, webresources, pcf controls, etc.)
    config.AddCommand<PushCommand>("push")
        .WithDescription("Build and register plugin assembly and web resources directly to DEV — skips pack/import. Reads [Step] attributes to create or update plugin registrations. Run after plugin or web resource changes.")
        .WithExample("push")
        .WithExample("push", "ContosoCustomizations")
        .WithExample("push", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/")
        .WithExample("push", "ContosoCustomizations --pluginFile ./bin/Release/Plugins.dll")
        .WithExample("push", "ContosoCustomizations --webresources ./dist")
        .WithExample("push", "ContosoCustomizations --pluginFile ./bin/Release/Plugins.dll --webresources ./dist");

    // Sync changes to local repo (export solution and unpack)
    config.AddCommand<SyncCommand>("sync")
          .WithDescription("Export solution from DEV, bump build version, and unpack to source-controlled XML. Run after testing changes in DEV. Requires no uncommitted changes in Package/src/.")
          .WithExample("sync")
          .WithExample("sync", "ContosoCustomizations")
          .WithExample("sync", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/ --managed")
          .WithExample("sync", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/");

    // Deploy (pack and import solution into environment)
    config.AddCommand<DeployCommand>("deploy")
          .WithDescription("Pack solution from repo and import into target environment (test, uat, prod, or URL). Requires clean git working directory.")
          .WithExample("deploy")
          .WithExample("deploy", "prod")
          .WithExample("deploy", "test")
          .WithExample("deploy", "https://contoso-test.crm4.dynamics.com/")
          .WithExample("deploy", "prod --solution ContosoCustomizations")
          .WithExample("deploy", "prod --solution ContosoCustomizations --managed");

    config.AddCommand<StatusCommand>("status")
          .WithDescription("Show configured environments, connection status, solution version, PAC CLI auth status, and git state. Use to verify setup before running commands.")
          .WithExample("status");

    // Generate early-bound C# types from solution entities via pac modelbuilder build
    config.AddCommand<GenerateCommand>("generate")
          .WithDescription("Generate early-bound C# types from solution entities and custom APIs. Overwrites Plugins/Models/ with generated .cs files. Run after adding or modifying entities or custom APIs.")
          .WithExample("generate")
          .WithExample("generate", "ContosoCustomizations")
          .WithExample("generate", "--namespace", "Contoso.Plugins.Models")
          .WithExample("generate", "--extra-tables", "account,contact");
});

return await app.RunAsync(args, cancellationTokenSource.Token);
