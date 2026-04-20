using Flowline;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using System.Reflection;

// Create a cancellation token source to handle Ctrl+C
var cancellationTokenSource = new CancellationTokenSource();

// Wire up Console.CancelKeyPress to trigger cancellation
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Prevent immediate process termination
    cancellationTokenSource.Cancel();
    Console.WriteLine("Cancellation requested...");
};

// Register services
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole());
services.AddSingleton<AnsiConsoleOutput>();
services.AddSingleton<IFlowlineOutput>(sp => sp.GetRequiredService<AnsiConsoleOutput>());
services.AddSingleton<IAssemblyAnalysisService, AssemblyAnalysisService>();
services.AddSingleton<IAuthenticationService, AuthenticationService>();
services.AddSingleton<IPluginSyncService, PluginSyncService>();
services.AddSingleton<IWebResourceSyncService, WebResourceSyncService>();
services.AddSingleton<ITranslationSyncService, TranslationSyncService>();

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
          .WithDescription("Use for bootstrapping an existing solution into the repo")
          .WithExample("clone", "ContosoCustomizations --prod https://contoso.crm4.dynamics.com")
          .WithExample("clone", "ContosoCustomizations --prod https://contoso.crm4.dynamics.com --managed");

    // copy/provision = Copy Source environment to destination environment
    config.AddCommand<ProvisionCommand>("provision")
          .WithDescription("Use to provision a real `dev` or `staging` environment from production.")
          .WithExample("provision")
          .WithExample("provision", "dev")
          .WithExample("provision", "staging")
          .WithExample("provision", "dev --prod https://contoso.crm4.dynamics.com")
          .WithExample("provision", "dev --copy full")
          .WithExample("provision", "dev --suffix mydev")
          .WithExample("provision", "dev --allow-overwrite");

    // Push assets to dev environment (upload and push assets to environment: plugins, webresources, pcf controls, etc.)
    config.AddCommand<PushCommand>("push")
        .WithDescription("Upload assets (webresources, plugins) to a Power Platform environment")
        .WithExample("push")
        .WithExample("push", "ContosoCustomizations")
        .WithExample("push", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/");

    // Sync changes to local repo (export solution and unpack)
    config.AddCommand<SyncCommand>("sync")
          .WithDescription("Use to synchronize the current solution in `dev` environment back into the local repo.")
          .WithExample("sync")
          .WithExample("sync", "ContosoCustomizations")
          .WithExample("sync", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/ --managed")
          .WithExample("sync", "ContosoCustomizations --dev https://contoso-dev.crm4.dynamics.com/");

    // Deploy (pack and import solution into environment)
    config.AddCommand<DeployCommand>("deploy")
          .WithDescription("Deploy the current solution in the local repo to an environment")
          .WithExample("deploy")
          .WithExample("deploy", "prod")
          .WithExample("deploy", "staging")
          .WithExample("deploy", "https://contoso-staging.crm4.dynamics.com/")
          .WithExample("deploy", "prod --solution ContosoCustomizations")
          .WithExample("deploy", "prod --solution ContosoCustomizations --managed");

    config.AddCommand<StatusCommand>("status")
          .WithDescription("Show current environment and the version of Flowline and Power Platform CLI")
          .WithExample("status");

    // Translation sync (export/import translations)
    config.AddCommand<TranslationCommand>("translations")
          .WithDescription("Export or import solution translations")
          .WithExample("translations", "export --solution ContosoCustomizations")
          .WithExample("translations", "import translations.zip");
});

return await app.RunAsync(args, cancellationTokenSource.Token);
