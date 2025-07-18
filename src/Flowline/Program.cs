﻿using Flowline.Commands;
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
          .WithDescription("Clone a Power Platform environment (similar to Clone/pull and Branch)")
          .WithExample("clone", "https://automatevalue.crm4.dynamics.com/");

    config.AddCommand<InitCommand>("init")
          .WithDescription("Initialize a local folder with git repository and Dataverse solution")
          .WithExample("init", "https://automatevalue.crm4.dynamics.com/");

    config.AddCommand<SyncCommand>("sync")
          .WithDescription("Sync a Power Platform solution (similar to Commit)")
          .WithExample("sync", "https://automatevalue-dev.crm4.dynamics.com/");

    config.AddCommand<PushToTestCommand>("push-to-test")
          .WithDescription("Push changes to test environment (similar to PullRequest)");

    config.AddCommand<MergeCommand>("merge")
          .WithDescription("Merge pull request into master");

    config.AddCommand<DeleteEnvCommand>("delete-env")
          .WithDescription("Delete a Power Platform environment");
});

return app.Run(args);
