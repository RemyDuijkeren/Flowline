using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Flowline.Tests")]

namespace Flowline.Services;

public class ProfileResolutionService(IAnsiConsole console, DataverseConnector dataverseConnector, FlowlineRuntimeOptions opt)
{
    /// <summary>Seam for testing — set to override FindBestProfile resolution.</summary>
    internal Func<string, ProfileResolutionResult>? FindBestProfileOverride { get; set; }

    public Task<PacProfile> ResolveAsync(string environmentUrl, CancellationToken cancellationToken = default)
    {
        var result = FindBestProfileOverride != null
            ? FindBestProfileOverride(environmentUrl)
            : dataverseConnector.FindBestProfile(environmentUrl);

        return result switch
        {
            ProfileFound found       => HandleFound(found.Profile),
            ProfileAmbiguous ambig   => HandleAmbiguousAsync(ambig.Candidates, environmentUrl, cancellationToken),
            ProfileNotFound notFound => throw BuildNotFoundError(notFound.EnvironmentUrl),
            _                        => throw new InvalidOperationException($"Unexpected ProfileResolutionResult: {result.GetType().Name}")
        };
    }

    Task<PacProfile> HandleFound(PacProfile profile)
    {
        EmitStatusLine(profile);

        if (opt.IsVerbose)
            console.Verbose($"Matched profile: {profile.Name ?? "(unnamed)"}, Kind: {profile.Kind}, URL: {profile.Resource}", isVerbose: true);

        return Task.FromResult(profile);
    }

    async Task<PacProfile> HandleAmbiguousAsync(IReadOnlyList<PacProfile> candidates, string environmentUrl, CancellationToken cancellationToken)
    {
        if (!ConsoleHelper.IsInteractive(settings: null))
        {
            var lines = string.Join("\n", candidates.Select(FormatCandidate));
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"Multiple PAC profiles match {environmentUrl} — run: pac auth select --index <n> to set one profile active\n{lines}");
        }

        var prompt = new SelectionPrompt<PacProfile>()
            .Title("Multiple PAC profiles match — select one:")
            .UseConverter(FormatCandidate)
            .AddChoices(candidates);

        var selected = await Task.Run(() => console.Prompt(prompt), cancellationToken);

        EmitStatusLine(selected);
        return selected;
    }

    FlowlineException BuildNotFoundError(string environmentUrl)
    {
        var suggestion = BuildNameSuggestion(environmentUrl);
        var url = environmentUrl.TrimEnd('/');
        return new FlowlineException(ExitCode.NotAuthenticated,
            $"No PAC auth profile found for {url}\nRun: pac auth create --environment {url} --name \"{suggestion}\"");
    }

    void EmitStatusLine(PacProfile profile)
    {
        var status = string.IsNullOrEmpty(profile.Name)
            ? $"Using PAC profile (unnamed, {profile.Kind}) — {profile.Resource}"
            : $"Using PAC profile '{profile.Name}' ({profile.Kind})";
        console.MarkupLine(status);
    }

    static string FormatCandidate(PacProfile p) =>
        string.IsNullOrEmpty(p.Name)
            ? $"(unnamed, {p.Kind}) — {p.Resource}"
            : $"'{p.Name}' ({p.Kind}) — {p.Resource}";

    internal static string BuildNameSuggestion(string environmentUrl)
    {
        // Extract host, take first segment before first dot
        if (!Uri.TryCreate(environmentUrl.Contains("://") ? environmentUrl : "https://" + environmentUrl, UriKind.Absolute, out var uri))
            return "MyOrg";

        var firstSegment = uri.Host.Split('.')[0]; // e.g. "automatevalue-dev"
        var parts = firstSegment.Split('-');
        return string.Join("-", parts.Select(p => p.Length > 0
            ? char.ToUpperInvariant(p[0]) + p[1..]
            : p));
    }
}
