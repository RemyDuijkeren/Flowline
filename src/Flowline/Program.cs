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

    config.AddCommand<InfoCommand>("info")
          .WithDescription("Show the version of Flowline and Power Platform CLI")
          .WithExample("info", "Displays the version of Flowline and Power Platform CLI");

    config.AddCommand<CloneCommand>("clone")
          .WithDescription("Clone a Power Platform environment (similar to Clone/pull and Branch)")
          .WithExample("clone", "https://automatevalue.crm4.dynamics.com/");

    config.AddCommand<InitCommand>("init")
          .WithDescription("Initialize a local folder with git repository and Dataverse solution")
          .WithExample("init", "https://automatevalue-dev.crm4.dynamics.com/");

    config.AddCommand<SyncCommand>("sync")
          .WithDescription("Sync a Power Platform solution to source control (similar to Git Commit and Push)")
          .WithExample("sync", "https://automatevalue-dev.crm4.dynamics.com/");

    config.AddCommand<DeployCommand>("deploy")
          .WithDescription("Deploy changes to test environment (similar to PullRequest)");

    config.AddCommand<MergeCommand>("merge")
          .WithDescription("Merge pull request into master");

    config.AddCommand<DeleteEnvCommand>("delete-env")
          .WithDescription("Delete a Power Platform environment");
});

return app.Run(args);
