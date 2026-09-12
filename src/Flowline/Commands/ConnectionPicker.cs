using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Commands;

/// <summary>
/// Asks which connection to bind, offering the environment's own (R9b, KTD20).
/// </summary>
/// <remarks>
/// A connection id is a generated string nobody can produce from memory, and the only place it was
/// readable is the maker portal, which is the round trip these commands exist to remove.
///
/// Shared by the two places that ask the question: binding one reference by name, and filling the blanks
/// a captured settings file left behind. One copy, because a second would be free to offer a different
/// menu for the same question.
/// </remarks>
internal static class ConnectionPicker
{
    /// <summary>Asks for a connection id, or nothing when the answer was to leave it alone.</summary>
    /// <remarks>
    /// <b>Only the caller's own connections are listed.</b> Connections belong to the person who made
    /// them, so one a colleague owns will not appear here. That is why typing an id by hand stays on the
    /// menu rather than being replaced by the list.
    ///
    /// <b>A failed listing narrows the menu, it does not end the run.</b> `pac` being unreachable is a
    /// reason to ask for the id instead of offering a list, not to fail a command that has already
    /// written to an environment or a file.
    /// </remarks>
    public static async Task<string?> PickAsync(
        IAnsiConsole console, EnvironmentInfo environment, string? connectorId, string title, CancellationToken ct)
    {
        while (true)
        {
            var connections = await console.Status().FlowlineSpinner().StartAsync(
                "Reading this environment's connections...",
                _ => PacConnections.ListAsync(environment.EnvironmentUrl!, ct));

            var matching = PacConnections.ForConnector(connections, connectorId);

            if (matching.Count == 0)
                console.Info("No connections here match that connector, or 'pac' couldn't list them.");

            var choices = matching
                .Select(c => new Choice($"{c.Name} ({c.Status})", ChoiceKind.Bind, c.Id))
                .Append(new Choice("Enter a connection id by hand", ChoiceKind.Type))
                .Append(new Choice("Create a new connection in the maker portal", ChoiceKind.Create))
                .Append(new Choice("Leave it as it is", ChoiceKind.Leave))
                .ToArray();

            var answer = await console.PromptAsync(
                new SelectionPrompt<Choice>()
                    .Title(FlowlineConsoleExtensions.Question(title))
                    .UseConverter(c => Markup.Escape(c.Label))
                    .AddChoices(choices), ct);

            switch (answer.Kind)
            {
                case ChoiceKind.Leave:
                    return null;

                case ChoiceKind.Type:
                    var typed = await console.PromptAsync(
                        new TextPrompt<string>(FlowlineConsoleExtensions.Question(
                            "Connection id (blank to leave it):")).AllowEmpty(), ct);

                    return string.IsNullOrEmpty(typed) ? null : typed;

                case ChoiceKind.Create:
                    OpenMakerPortal(console, environment.EnvironmentId);

                    // Declining is the way out of the loop: someone who did not create a connection after
                    // all would otherwise have only Ctrl+C.
                    if (!await console.PromptAsync(
                            new ConfirmationPrompt("Created it? Answer yes to list the connections again"), ct))
                        return null;

                    continue;

                default:
                    return answer.ConnectionId;
            }
        }
    }

    /// <summary>Opens the environment's new-connection page in the default browser.</summary>
    /// <remarks>
    /// The portal is the only place most connections can be created: a connector that needs an interactive
    /// consent has no headless path, and `pac connection create` makes a service principal Dataverse
    /// connection and nothing else.
    ///
    /// A browser that will not open is reported rather than thrown. The address is printed either way, so
    /// the operator can open it themselves.
    /// </remarks>
    static void OpenMakerPortal(IAnsiConsole console, Guid environmentId)
    {
        var url = $"https://make.powerapps.com/environments/{environmentId}/connections/available";

        console.Info($"Opening {url}");

        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            console.Warning($"Couldn't open a browser ({Markup.Escape(ex.Message)}). Open that address yourself.");
        }
    }

    enum ChoiceKind { Bind, Type, Create, Leave }

    sealed record Choice(string Label, ChoiceKind Kind, string? ConnectionId = null);
}
