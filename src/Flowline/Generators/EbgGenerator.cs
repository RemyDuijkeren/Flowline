using System.Runtime.CompilerServices;
using DLaB.EarlyBoundGeneratorV2;
using DLaB.Log;
using DLaB.EarlyBoundGeneratorV2.Settings;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Spectre.Console;

[assembly: InternalsVisibleTo("Flowline.Tests")]

namespace Flowline.Generators;

/// <summary>
/// Early Bound Generator V2 (Daryl LaBar), run in-process.
///
/// Unlike every other generator this one does not shell out. EBG's <see cref="Logic"/> drives
/// Microsoft's own <c>ModelBuilderLib</c> — the same engine behind <c>pac modelbuilder build</c> —
/// with DLaB provider classes swapped in for casing and filtering. PAC CLI is not involved.
///
/// ModelBuilderLib resolves those providers with <c>Type.GetType(name)</c>, so the assembly-qualified
/// names in the settings file are loaded from our own app base via deps.json. That is why
/// <c>xrmToolBoxPluginPath</c> plays no part in resolution here, and why the DLL-copying that the
/// PAC CLI route requires is unnecessary.
/// </summary>
public class EbgGenerator(IAnsiConsole console) : IGenerator
{
    public GeneratorType Type => GeneratorType.Ebg;

    public async Task RunAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        var entityTask = console.Status().FlowlineSpinner().StartAsync(
            "Discovering solution entities...",
            _ => GenerateReader.GetSolutionEntityLogicalNamesAsync(context.Service, context.RemoteSolution.Id, cancellationToken));
        var customApiTask = GenerateReader.GetSolutionCustomApiMessageNamesAsync(context.Service, context.RemoteSolution.Id, cancellationToken);

        var solutionEntities = await entityTask;
        var customApiNames = await customApiTask;

        var entityFilter = solutionEntities
            .Concat(context.ExtraTables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        console.Ok($"Found [bold]{entityFilter.Count}[/] entities" +
                   (customApiNames.Count > 0 ? $", [bold]{customApiNames.Count}[/] custom APIs" : ""));

        if (context.Verbose)
        {
            foreach (var entity in entityFilter)
                console.Verbose($"  entity: {entity}");
            foreach (var api in customApiNames)
                console.Verbose($"  custom api: {api}");
        }

        // EBG writes its settings file, and on failure its log, beside itself. Both belong in temp,
        // never in the output folder — the shared tail copies everything under TempOutputPath into
        // the user's Models/ folder.
        var workDir = Path.Combine(Path.GetTempPath(), $"flowline-ebg-{Guid.NewGuid()}");

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(context.TempOutputPath);

            var settingsPath = Path.Combine(workDir, "builderSettings.json");

            // Escape hatch: a project-level builderSettings.json seeds the file, then
            // UpdateBuilderSettingsJson overwrites the keys Flowline derives. Anything the user set
            // that Flowline does not manage survives; anything it does manage is Flowline's.
            if (context.BuilderSettingsPath is { } userSettings && File.Exists(userSettings))
            {
                File.Copy(userSettings, settingsPath);
                console.Info($"Merging settings from [bold]{Path.GetFileName(userSettings)}[/]");
            }

            var config = BuildConfig(context.ModelNamespace, context.ServiceContextName, context.TempOutputPath,
                entityFilter, customApiNames, settingsPath, workDir);

            var logic = new Logic(config);
            logic.UpdateBuilderSettingsJson();

            // Create() redirects Console.Out into DLaB's own logger, so everything ModelBuilder and the
            // DLaB providers report — including the failure detail Create reads back out of its log file
            // — is only reachable through this event. Buffer it rather than writing through, because the
            // status spinner owns the console until the run returns.
            var output = new List<string>();
            void Collect(LogMessageInfo info)
            {
                lock (output)
                {
                    if (!string.IsNullOrWhiteSpace(info.Detail)) output.Add(info.Detail.TrimEnd());
                    if (!string.IsNullOrWhiteSpace(info.ModalMessage)) output.Add(info.ModalMessage.TrimEnd());
                }
            }

            bool succeeded;
            Logger.Instance.OnLog += Collect;
            try
            {
                succeeded = await console.Status().FlowlineSpinner().StartAsync(
                    $"Generating early-bound types into [bold]{context.OutputLabel}[/]...",
                    _ => Task.Run(() => logic.Create(context.Service), cancellationToken));
            }
            finally
            {
                Logger.Instance.OnLog -= Collect;
            }

            if (!succeeded)
            {
                foreach (var line in output)
                    console.WriteLine(line);

                throw new FlowlineException(ExitCode.BuildFailed, "Early Bound Generator failed — see the output above.");
            }

            if (context.Verbose)
                foreach (var line in output)
                    console.Verbose(line);

            console.Ok("Early-bound types generated");
        }
        finally
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// Maps Flowline's derived inputs onto EBG's defaults. Everything not set here stays at
    /// <see cref="EarlyBoundGeneratorConfig.GetDefault"/>, which is the opinionated-defaults stance:
    /// EBG has 50+ knobs and Flowline documents none of them.
    /// </summary>
    internal static EarlyBoundGeneratorConfig BuildConfig(
        string modelNamespace,
        string? serviceContextName,
        string tempOutputPath,
        IReadOnlyList<string> entityFilter,
        IReadOnlyList<string> customApiNames,
        string settingsPath,
        string workDir)
    {
        var config = EarlyBoundGeneratorConfig.GetDefault();

        config.RootPath = tempOutputPath;
        config.Namespace = modelNamespace;
        config.ServiceContextName = serviceContextName ?? "XrmContext";
        config.AudibleCompletionNotification = false;

        // Rooted paths are taken as-is, relative ones are combined with RootPath (the output folder).
        config.ExtensionConfig.BuilderSettingsJsonRelativePath = settingsPath;

        // XrmToolBoxPluginPath is only a root for EBG's own file lookups here, never for provider
        // resolution. Pointing it at workDir keeps a failed run's log out of the user's repo.
        config.ExtensionConfig.XrmToolBoxPluginPath = workDir;

        // The casing dictionary ships in lib/ beside our assembly, not in the DLaB.EarlyBoundGeneratorV2
        // subfolder the default relative path assumes. Pin the absolute path so it resolves regardless.
        config.ExtensionConfig.CamelCaseNamesDictionaryRelativePath =
            Path.Combine(AppContext.BaseDirectory, "DLaB.Dictionary.txt");
        config.ExtensionConfig.TransliterationRelativePath =
            Path.Combine(AppContext.BaseDirectory, "alphabets");

        // Flowline already knows what the solution contains, so the filter replaces EBG's default
        // list of standard tables rather than adding to it.
        config.ExtensionConfig.EntitiesWhitelist = string.Join("|", entityFilter);
        config.ExtensionConfig.EntityPrefixesWhitelist = null;

        config.GenerateMessages = customApiNames.Count > 0;
        config.ExtensionConfig.ActionsWhitelist = customApiNames.Count > 0 ? string.Join("|", customApiNames) : null;
        config.ExtensionConfig.ActionPrefixesWhitelist = null;

        return config;
    }
}
