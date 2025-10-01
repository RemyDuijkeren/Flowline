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

    config.AddCommand<CloneCommand>("clone")
          .WithDescription("Clone a local folder with a remote git repository and Dataverse solution")
          .WithExample("clone", "https://github.com/AutomateValue/Dataverse01.git https://automatevalue-dev.crm4.dynamics.com");

    config.AddCommand<BranchEnvCommand>("branch-env")
          .WithDescription("Branch a Power Platform production environment by coping into a new environment and create a new branch in the git repository")
          .WithExample("branch-env", "https://automatevalue.crm4.dynamics.com/");

    config.AddCommand<SyncCommand>("sync")
          .WithDescription("Sync a Power Platform solution to source control (similar to Git Commit and Push)")
          .WithExample("sync", "https://automatevalue-dev.crm4.dynamics.com/");

    config.AddCommand<InfoCommand>("info")
          .WithDescription("Show the version of Flowline and Power Platform CLI")
          .WithExample("info", "Displays the version of Flowline and Power Platform CLI");

    config.AddCommand<EnvCommand>("env")
          .WithDescription("Manage and switch between environments")
          .WithExample("env", "Show current environment configuration")
          .WithExample("env prod", "Switch to production environment")
          .WithExample("env dev", "Switch to development environment");

    config.AddCommand<DeployCommand>("deploy")
          .WithDescription("Deploy changes to test environment (similar to PullRequest)");

    config.AddCommand<MergeCommand>("merge")
          .WithDescription("Merge pull request into master");

    config.AddCommand<DeleteEnvCommand>("delete-env")
          .WithDescription("Delete a Power Platform environment");
});

return app.Run(args);
