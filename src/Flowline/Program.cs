using Flowline;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using System.Reflection;

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
services.AddSingleton<AnsiConsoleOutput>();
services.AddSingleton<IFlowlineOutput>(sp => sp.GetRequiredService<AnsiConsoleOutput>());
services.AddSingleton<AssemblyAnalysisService>();
services.AddSingleton<AuthenticationService>();
services.AddSingleton<PluginRegistrationService>();
services.AddSingleton<WebResourceSyncService>();
services.AddSingleton<TranslationSyncService>();

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

    // clone = Clone solution from environment to local folder
    config.AddCommand<CloneCommand>("clone") // init (new repo) or clone (existing repo)
          .WithDescription("Clone an existing solution into this repo")
          .WithExample("clone", "ContosoCustomizations --prod https://contoso.crm4.dynamics.com")
          .WithExample("clone", "ContosoCustomizations --prod https://contoso.crm4.dynamics.com --managed");

    // copy/provision = Copy Source environment to destination environment
    config.AddCommand<ProvisionCommand>("provision")
          .WithDescription("Copy prod into dev or staging environment")
          .WithExample("provision")
          .WithExample("provision", "dev")
          .WithExample("provision", "staging")
          .WithExample("provision", "dev --prod https://contoso.crm4.dynamics.com")
          .WithExample("provision", "dev --copy full")
          .WithExample("provision", "dev --suffix mydev")
          .WithExample("provision", "dev --allow-overwrite");

    // Push assets to dev environment (upload and push assets to environment: plugins, webresources, pcf controls, etc.)
    config.AddCommand<PushCommand>("push")
        .WithDescription("Push plugins and web resources to Dataverse")
        .WithExample("push")
        .WithExample("push", "ContosoCustomizations")
        .WithExample("push", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/");

    // Sync changes to local repo (export solution and unpack)
    config.AddCommand<SyncCommand>("sync")
          .WithDescription("Pull dev changes back into the repo")
          .WithExample("sync")
          .WithExample("sync", "ContosoCustomizations")
          .WithExample("sync", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/ --managed")
          .WithExample("sync", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/");

    // Deploy (pack and import solution into environment)
    config.AddCommand<DeployCommand>("deploy")
          .WithDescription("Deploy solution to staging or prod environment")
          .WithExample("deploy")
          .WithExample("deploy", "prod")
          .WithExample("deploy", "staging")
          .WithExample("deploy", "https://contoso-staging.crm4.dynamics.com/")
          .WithExample("deploy", "prod --solution ContosoCustomizations")
          .WithExample("deploy", "prod --solution ContosoCustomizations --managed");

    config.AddCommand<StatusCommand>("status")
          .WithDescription("Show Flowline, PAC CLI, and project status")
          .WithExample("status");

    // Translation sync (export/import translations)
    config.AddCommand<TranslationCommand>("translations")
          .WithDescription("Export or import solution translations")
          .WithExample("translations", "export --solution ContosoCustomizations")
          .WithExample("translations", "import translations.zip");
});

return await app.RunAsync(args, cancellationTokenSource.Token);
