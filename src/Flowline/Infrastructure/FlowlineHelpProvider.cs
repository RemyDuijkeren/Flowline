using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Help;
using Spectre.Console.Rendering;

namespace Flowline.Infrastructure;

/// <summary>
/// Spectre's built-in help, plus a Flowline banner on the root screen and a docs link at the bottom.
/// </summary>
/// <remarks>
/// Register with the instance overload — <c>config.SetHelpProvider(new FlowlineHelpProvider(config.Settings))</c>
/// — and only after <c>SetApplicationName</c>/<c>SetApplicationVersion</c>: the base constructor snapshots
/// the settings. The generic <c>SetHelpProvider&lt;T&gt;()</c> overload resolves through the
/// <see cref="TypeRegistrar"/>, which has no <see cref="ICommandAppSettings"/> registration.
/// </remarks>
internal sealed class FlowlineHelpProvider(ICommandAppSettings settings) : HelpProvider(settings)
{
    internal const string DocsUrl = "https://github.com/RemyDuijkeren/Flowline/wiki";

    /// <summary>
    /// Repaints the section headers (DESCRIPTION, USAGE, …) in the Flowline welcome colour, leaving the
    /// rest of Spectre's styling alone. Call before constructing the provider — the base constructor
    /// snapshots the styles.
    /// </summary>
    /// <remarks>
    /// Mutates in place. Unless <c>HelpProviderStyles</c> was replaced first, that object is
    /// <see cref="HelpProviderStyle.Default"/> — a shared singleton — so this changes the default for the
    /// whole process. Fine for a CLI that configures help once at startup.
    /// </remarks>
    internal static void UseFlowlineHeaderColor(HelpProviderStyle styles)
    {
        var header = new Style(ConsoleHelper.s_welcomeColor);

        // Default populates all six sub-styles, so no null checks.
        styles.Description!.Header = header;
        styles.Usage!.Header = header;
        styles.Examples!.Header = header;
        styles.Arguments!.Header = header;
        styles.Options!.Header = header;
        styles.Commands!.Header = header;
    }

    public override IEnumerable<IRenderable> GetHeader(ICommandModel model, ICommandInfo? command)
    {
               
        if (command is null)
        {
            var welcomeText = new Text(ConsoleHelper.s_logo, new Style(ConsoleHelper.s_welcomeColor));
            
            yield return welcomeText;
            yield return Text.NewLine;
        }
        
        var version = FlowlineVersion.Display;
        var versionText = new Text($"Flowline CLI v{version} - Dataverse ALM: clone → push → sync → deploy", new Style(ConsoleHelper.s_welcomeColor));
        yield return versionText;
        yield return Text.NewLine;
        yield return Text.NewLine;
    }

    public override IEnumerable<IRenderable> GetFooter(ICommandModel model, ICommandInfo? command)
    {
        yield return Text.NewLine;
        yield return new Markup($"[dim][link={DocsUrl}]Docs: {DocsUrl}[/][/]");
        yield return Text.NewLine;
    }
}
