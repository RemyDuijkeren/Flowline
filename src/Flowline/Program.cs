using Flowline.Commands;
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

// Configure and run the app
var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("flowline");
    config.SetApplicationVersion(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "1.0.0");
#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif

    config.AddCommand<CloneCommand>("clone") // init (new repo) or clone (existing repo)
          .WithDescription("Init a local folder by cloning a remote git repository and Dataverse solution")
          .WithExample("clone", "https://github.com/contoso/Dataverse01.git --environment https://contoso.crm4.dynamics.com")
          .WithExample("clone", "https://github.com/contoso/Dataverse01.git --environment https://contoso.crm4.dynamics.com --solution ContosoCustomizations")
          .WithExample("clone", "https://github.com/contoso/Dataverse01.git --environment https://contoso.crm4.dynamics.com --solution ContosoCustomizations --managed");

    // copy  = Copy Source environment to destination environment
    // clone = Clone solution from environment to local folder
    config.AddCommand<ProvisionCommand>("prime") // prime from PROD, use also reset (https://learn.microsoft.com/en-us/power-platform/admin/reset-environment)?
          .WithDescription("Branch a Power Platform production environment by coping into a new environment and create a new branch in the git repository")
          .WithExample("prime")
          .WithExample("prime", "dev")
          .WithExample("prime", "dev --fullcopy")
          .WithExample("prime", "dev --suffix mydev")
          .WithExample("prime", "dev --environment https://contoso-dev.crm4.dynamics.com")
          .WithExample("prime", "staging")
          .WithExample("prime", "dev --source https://contoso.crm4.dynamics.com");

    // push (upload and push assets to environment: plugins, webresources, pcf controls, etc.)
    config.AddCommand<PushCommand>("push")
        .WithDescription("Upload assets (webresources, plugins) to a Power Platform environment")
        .WithExample("push")
        .WithExample("push", "https://contoso-dev.crm4.dynamics.com/");

    // export or snapshot
    config.AddCommand<SyncCommand>("export")
          .WithDescription("Export a Power Platform solution to source control)")
          .WithExample("export")
          .WithExample("export", "https://contoso-dev.crm4.dynamics.com/");

    // sync = push and export

    config.AddCommand<StageCommand>("stage")
          .WithDescription("Deploy changes to staging environment (similar to PullRequest)");

    // Ship or Release
    config.AddCommand<ReleaseCommand>("release")
          .WithDescription("Release changes to production environment");

    config.AddCommand<StatusCommand>("status")
          .WithDescription("Show current environment and the version of Flowline and Power Platform CLI")
          .WithExample("status");
});

return await app.RunAsync(args, cancellationTokenSource.Token);
