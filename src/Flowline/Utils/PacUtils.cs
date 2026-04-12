using Flowline.Utils;
using CliWrap;
using CliWrap.Buffered;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Spectre.Console;

[assembly: InternalsVisibleTo("Flowline.Tests")]

namespace Flowline;

public static class PacUtils
{
    static (string Command, string[]? PrefixArgs, bool IsDotnetTool)? _cachedPacCommand;
    internal static Func<string, string[], CancellationToken, Task<(bool Success, string Output)>>? CheckCommandExistsFunc { get; set; }

    internal static void ResetCache()
    {
        _cachedPacCommand = null;
    }

    static async Task<(bool Success, string Output)> CheckCommandExistsAsync(string command, string[]? args, CancellationToken cancellationToken)
    {
        if (CheckCommandExistsFunc != null)
        {
            return await CheckCommandExistsFunc(command, args ?? Array.Empty<string>(), cancellationToken);
        }

        try
        {
            var cmd = Cli.Wrap(command);
            if (args != null)
            {
                cmd = cmd.WithArguments(args);
            }
            var result = await cmd.ExecuteBufferedAsync(cancellationToken);
            return (result.ExitCode == 0, result.StandardOutput + result.StandardError);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    public static async Task<(string Version, string InstallType)> AssertPacCliInstalledAsync(bool verbose = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var (command, prefixArgs, isDotnetTool) = await GetBestPacCommandAsync(cancellationToken);

            var installType = command switch
            {
                "pac.exe" => "Dotnet Tool (.NET)",
                "pac" when isDotnetTool => "Dotnet Tool (.NET)",
                "pac" when !isDotnetTool => "MSI Installer (.NET Framework)",
                "dnx" => ".NET 10 dnx (One-shot)",
                "pac.launcher.exe" => "MSI Installer (.NET Framework)",
                _ => "Unknown"
            };

            //Console.WriteLine($"Detected Power Platform CLI installation type: {installType}");
            //Console.WriteLine($"Using command: {command}");

            var result = await Cli.Wrap(command).WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("help"))
                .ExecuteBufferedAsync(cancellationToken);

            // Extract version from the output
            var versionLine = result.StandardOutput.Split('\n')
                .FirstOrDefault(line => line.Trim().StartsWith("Version:"));

            if (versionLine != null)
            {
                var version = versionLine.Trim().Replace("Version: ", string.Empty);
                if (verbose)
                {
                    AnsiConsole.MarkupLine($"[dim]Power Platform CLI version: {version} ({installType})[/]");
                }
                return (version, installType);
            }

            return ("Unknown", installType);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            if (ex.Message.Contains("MSI-installed Power Platform CLI"))
            {
                Console.Error.WriteLine(ex.Message);
            }
            else
            {
                Console.Error.WriteLine("The Power Platform CLI (pac) is not installed or not in PATH.");
                Console.Error.WriteLine("Please install it using the dotnet tool method (Recommended):");
                Console.Error.WriteLine("  dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
            }
            Environment.Exit(1);
            return (string.Empty, string.Empty);
        }
    }

    public static async Task<(string Command, string[]? PrefixArgs, bool IsDotnetTool)> GetBestPacCommandAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedPacCommand != null) return _cachedPacCommand.Value;

        // 1. Check for 'pac.exe' (explicitly the dotnet tool version)
        var pacExeCheck = await CheckCommandExistsAsync("pac.exe", new[] { "help" }, cancellationToken);
        //Console.WriteLine($"pac.exe check: {pacExeCheck.Success}, Output: {pacExeCheck.Output}");
        if (pacExeCheck.Success)
        {
            _cachedPacCommand = ("pac.exe", null, true);
            return _cachedPacCommand.Value;
        }

        // 2. Check for 'pac' (could be dotnet tool or MSI)
        var pacCheck = await CheckCommandExistsAsync("pac", new[] { "help" }, cancellationToken);
        //Console.WriteLine($"pac check: {pacCheck.Success}, Output: {pacCheck.Output}");
        if (pacCheck.Success)
        {
            // Check if it is the dotnet tool version (.NET vs .NET Framework)
            bool isDotnetTool = pacCheck.Output.Contains(".NET") && !pacCheck.Output.Contains(".NET Framework");

            if (isDotnetTool)
            {
                _cachedPacCommand = ("pac", null, true);
                return _cachedPacCommand.Value;
            }

            // It's the MSI version, we check if 'dnx' is available as a better alternative
            var dnxCheck = await CheckCommandExistsAsync("dnx", new[] { "microsoft.powerapps.cli.tool", "help", "--yes" }, cancellationToken);
            //Console.WriteLine($"dnx check: {dnxCheck.Success}, Output: {dnxCheck.Output}");
            if (dnxCheck.Success)
            {
                _cachedPacCommand = ("dnx", new[] { "microsoft.powerapps.cli.tool", "--yes" }, true);
                return _cachedPacCommand.Value;
            }

            // We do NOT fallback to MSI version anymore as it's unreliable
            throw new Exception("Only the MSI-installed Power Platform CLI was found, but it is not supported by Flowline due to inaccurate exit codes. Please install the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
        }

        // 3. Check for 'pac.launcher.exe' (explicitly the MSI version)
        var msiCheck = await CheckCommandExistsAsync("pac.launcher.exe", new[] { "help" }, cancellationToken);
        //Console.WriteLine($"pac.launcher.exe check: {msiCheck.Success}, Output: {msiCheck.Output}");
        if (msiCheck.Success)
        {
            // Check if 'dnx' is available as a better alternative
            var dnxCheck = await CheckCommandExistsAsync("dnx", new[] { "microsoft.powerapps.cli.tool", "help", "--yes" }, cancellationToken);
            //Console.WriteLine($"dnx check: {dnxCheck.Success}, Output: {dnxCheck.Output}");
            if (dnxCheck.Success)
            {
                _cachedPacCommand = ("dnx", new[] { "microsoft.powerapps.cli.tool", "--yes" }, true);
                return _cachedPacCommand.Value;
            }

            throw new Exception("Only the MSI-installed Power Platform CLI was found, but it is not supported by Flowline due to inaccurate exit codes. Please install the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
        }

        // 4. Fallback to 'dnx microsoft.powerapps.cli.tool' if nothing else worked
        var dnxOnlyCheck = await CheckCommandExistsAsync("dnx", new[] { "microsoft.powerapps.cli.tool", "help", "--yes" }, cancellationToken);
        //Console.WriteLine($"dnx check: {dnxOnlyCheck.Success}, Output: {dnxOnlyCheck.Output}");
        if (dnxOnlyCheck.Success)
        {
            _cachedPacCommand = ("dnx", new[] { "microsoft.powerapps.cli.tool", "--yes" }, true);
            return _cachedPacCommand.Value;
        }

        throw new Exception("Power Platform CLI is not installed.");
    }

    public static async Task<List<EnvironmentInfo>> GetEnvironmentsAsync(bool verbose = true, CancellationToken cancellationToken = default)
    {
        var (cmdName, prefixArgs, _) = await GetBestPacCommandAsync(cancellationToken);
        var result = await Cli.Wrap(cmdName).WithArguments(args => args
            .AddIfNotNull(prefixArgs)
            .Add("admin")
            .Add("list")
            .Add("--json"))
            .WithToolExecutionLog(verbose)
            .ExecuteBufferedAsync(cancellationToken);

        return JsonSerializer.Deserialize<List<EnvironmentInfo>>(result.StandardOutput) ?? new List<EnvironmentInfo>();
    }

    public static async Task<EnvironmentInfo?> GetEnvironmentInfoByUrlAsync(string environmentUrl, bool verbose = true, CancellationToken cancellationToken = default)
    {
        var environments = await GetEnvironmentsAsync(verbose, cancellationToken);
        return environments.FirstOrDefault(e => e.EnvironmentUrl?.TrimEnd('/').Equals(environmentUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true);
    }

    public static async Task<List<SolutionInfo>> GetSolutionsAsync(string environmentUrl, bool verbose = true, CancellationToken cancellationToken = default)
    {
        var (cmdName, prefixArgs, _) = await GetBestPacCommandAsync(cancellationToken);
        var result = await Cli.Wrap(cmdName).WithArguments(args => args
            .AddIfNotNull(prefixArgs)
            .Add("solution")
            .Add("list")
            .Add("--environment")
            .Add(environmentUrl)
            .Add("--json"))
            .WithToolExecutionLog(verbose)
            .ExecuteBufferedAsync(cancellationToken);

        return JsonSerializer.Deserialize<List<SolutionInfo>>(result.StandardOutput) ?? new List<SolutionInfo>();
    }

    public static EnvironmentUrlParts GetPartsFromEnvUrl(string envUrl)
    {
        var regex = new Regex(@"^https://([^.]+)\.([^.]+\.[^.]+\.[a-z]+)(?:/|$)");
        var match = regex.Match(envUrl);

        if (!match.Success)
        {
            Console.Error.WriteLine($"Could not extract environment name and region domain from URL: {envUrl}");
            Environment.Exit(1);
        }

        var organization = match.Groups[1].Value;
        var host = match.Groups[2].Value;

        var hostToRegion = new Dictionary<string, string>
        {
            { "crm.dynamics.com", "unitedstates" },
            { "crm3.dynamics.com", "canada" },
            { "crm2.dynamics.com", "southamerica" },
            { "crm4.dynamics.com", "europe" },
            { "crm12.dynamics.com", "france" },
            { "crm.microsoftdynamics.de", "germany" },
            { "crm21.dynamics.com", "switzerland" },
            { "crm11.dynamics.com", "unitedkingdom" },
            { "crm22.dynamics.com", "norway" },
            { "crm5.dynamics.com", "asia" },
            { "crm6.dynamics.com", "japan" },
            { "crm8.dynamics.com", "australia" },
            { "crm9.dynamics.com", "india" },
            { "crm20.dynamics.com", "uae" },
            { "crm19.dynamics.com", "korea" },
            { "crm.dynamics.cn", "china" },
            { "crm.appsplatform.us", "usgovhigh" }
        };

        if (!hostToRegion.TryGetValue(host, out var region))
        {
            Console.Error.WriteLine($"Unknown region/domain: {host}");
            Environment.Exit(1);
        }

        return new EnvironmentUrlParts
        {
            Organization = organization,
            Host = host,
            Region = region
        };
    }
}

public class EnvironmentInfo
{
    public Guid EnvironmentId { get; set; }
    public string? EnvironmentUrl { get; set; }
    public Guid OrganizationId { get; set; }
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public string? DomainName { get; set; }
    public string? Version { get; set; }
}

public class EnvironmentUrlParts
{
    public string Organization { get; set; } = null!;
    public string Host { get; set; } = null!;
    public string Region { get; set; } = null!;
}

public class SolutionInfo
{
    public Guid Id { get; set; }
    public string? SolutionUniqueName { get; set; }
    public string? FriendlyName { get; set; }
    public string? PublisherUniqueName { get; set; }
    public string? VersionNumber { get; set; }
    public bool IsManaged { get; set; }
}
