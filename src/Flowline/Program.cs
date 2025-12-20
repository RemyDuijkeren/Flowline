using Flowline.Commands;
using Spectre.Console.Cli;
using System.Reflection;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("flowline");
    config.SetApplicationVersion(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "1.0.0");
#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif

    config.AddCommand<InitCommand>("init") // init (new repo) or clone (existing repo)
          .WithDescription("Init a local folder by cloning a remote git repository and Dataverse solution")
          .WithExample("init", "https://github.com/contoso/Dataverse01.git --environment https://contoso.crm4.dynamics.com")
          .WithExample("init", "https://github.com/contoso/Dataverse01.git --environment https://contoso.crm4.dynamics.com --solution ContosoCustomizations")
          .WithExample("init", "https://github.com/contoso/Dataverse01.git --environment https://contoso.crm4.dynamics.com --solution ContosoCustomizations --managed");

    config.AddCommand<PrimeCommand>("prime") // prime from PROD, use also reset (https://learn.microsoft.com/en-us/power-platform/admin/reset-environment)?
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
    config.AddCommand<ExportCommand>("export")
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

return app.Run(args);
