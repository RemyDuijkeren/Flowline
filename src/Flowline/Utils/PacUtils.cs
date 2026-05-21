using Flowline.Config;
using Flowline.Utils;
using CliWrap;
using CliWrap.Buffered;
using System.Diagnostics;
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
                AnsiConsole.MarkupLine("PAC CLI's good");
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
                Console.Error.WriteLine("Power Platform CLI (pac) isn't available.");
                Console.Error.WriteLine("Install the dotnet tool version:");
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
            throw new Exception("Only the MSI-installed Power Platform CLI was found. Flowline needs the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
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

            throw new Exception("Only the MSI-installed Power Platform CLI was found. Flowline needs the dotnet tool version: dotnet tool install -g Microsoft.PowerApps.CLI.Tool");
        }

        // 4. Fallback to 'dnx microsoft.powerapps.cli.tool' if nothing else worked
        var dnxOnlyCheck = await CheckCommandExistsAsync("dnx", new[] { "microsoft.powerapps.cli.tool", "help", "--yes" }, cancellationToken);
        //Console.WriteLine($"dnx check: {dnxOnlyCheck.Success}, Output: {dnxOnlyCheck.Output}");
        if (dnxOnlyCheck.Success)
        {
            _cachedPacCommand = ("dnx", new[] { "microsoft.powerapps.cli.tool", "--yes" }, true);
            return _cachedPacCommand.Value;
        }

        throw new Exception("Power Platform CLI isn't available.");
    }

    public static async Task<int> PackSolutionAsync(ProjectSolution projectSln, string slnFolder, string binFolder, bool managed, bool verbose, CancellationToken cancellationToken)
    {
        var packageType = managed ? "Managed" : "Unmanaged";
        var suffix = managed ? "_managed" : "_unmanaged";
        var zipFile = Path.Combine(binFolder, $"{projectSln.Name}{suffix}.zip");

        Directory.CreateDirectory(binFolder);

        var sw = Stopwatch.StartNew();
        var (cmdName, prefixArgs, _) = await GetBestPacCommandAsync(cancellationToken);
        CommandResult result = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Validating {packageType.ToLower()} package...",
            ctx => Cli.Wrap(cmdName)
                      .WithArguments(args =>
                          args.AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("pack")
                              .Add("--folder").Add(Path.Combine(slnFolder, "src"))
                              .Add("--zipFile").Add(zipFile)
                              .Add("--packageType").Add(packageType))
                      .WithValidation(CommandResultValidation.None)
                      .WithToolExecutionLog(verbose, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);
        sw.Stop();

        if (!result.IsSuccess)
        {
            AnsiConsole.MarkupLine($"[red]{packageType} pack failed — check your solution source.[/]");
            return 1;
        }

        var duration = sw.Elapsed.TotalMinutes >= 1
            ? $"{(int)sw.Elapsed.TotalMinutes}m {sw.Elapsed.Seconds}s"
            : $"{(int)sw.Elapsed.TotalSeconds}s";
        AnsiConsole.MarkupLine($"[green]✓[/] {packageType} package validated in {duration}");
        return 0;
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

    public static async Task<string?> GetPublisherCustomizationPrefixAsync(string environmentUrl, string publisherUniqueName, bool verbose = false, CancellationToken cancellationToken = default)
    {
        var fetchXml = $"<fetch><entity name='publisher'><attribute name='customizationprefix'/>" +
                       $"<filter><condition attribute='uniquename' operator='eq' value='{publisherUniqueName}'/>" +
                       $"</filter></entity></fetch>";

        var (cmdName, prefixArgs, _) = await GetBestPacCommandAsync(cancellationToken);
        var result = await Cli.Wrap(cmdName)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("env").Add("fetch")
                .Add("--environment").Add(environmentUrl)
                .Add("--xml").Add(fetchXml))
            .WithToolExecutionLog(verbose)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        var allLines = result.StandardOutput
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        // PAC always appends publisherid column — find header explicitly, take next non-empty line as data
        var headerIdx = allLines.FindIndex(l => l.TrimStart().StartsWith("customizationprefix", StringComparison.OrdinalIgnoreCase));
        if (headerIdx < 0) return null;

        var header = allLines[headerIdx];
        var data = allLines.Skip(headerIdx + 1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (data == null) return null;

        var publisherIdPos = header.IndexOf("publisherid", StringComparison.OrdinalIgnoreCase);
        var prefix = publisherIdPos > 0
            ? data[..Math.Min(publisherIdPos, data.Length)].Trim()
            : data.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return string.IsNullOrEmpty(prefix) ? null : prefix;
    }

    internal static string? ParseVersionFromPacOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("Solution Version:"));
        if (line == null) return null;

        var parts = line.Split(": ", 2);
        return parts.Length == 2 ? parts[1].Trim() : null;
    }

    public static async Task<string> GetSolutionVersionAsync(string solutionName, string environmentUrl, bool verbose = false, CancellationToken cancellationToken = default)
    {
        var (cmdName, prefixArgs, _) = await GetBestPacCommandAsync(cancellationToken);
        var result = await Cli.Wrap(cmdName)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("solution")
                .Add("online-version")
                .Add("--solution-name").Add(solutionName)
                .Add("--environment").Add(environmentUrl))
            .WithValidation(CommandResultValidation.None)
            .WithToolExecutionLog(verbose)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
            throw new FlowlineException("Failed to read solution version from Dataverse.");

        var version = ParseVersionFromPacOutput(result.StandardOutput);
        if (string.IsNullOrEmpty(version))
            throw new FlowlineException("Could not parse solution version from PAC output.");

        return version;
    }

    public static async Task SetSolutionVersionAsync(string solutionName, string version, string environmentUrl, bool verbose = false, CancellationToken cancellationToken = default)
    {
        var (cmdName, prefixArgs, _) = await GetBestPacCommandAsync(cancellationToken);
        var result = await Cli.Wrap(cmdName)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("solution")
                .Add("online-version")
                .Add("--solution-name").Add(solutionName)
                .Add("--environment").Add(environmentUrl)
                .Add("--solution-version").Add(version))
            .WithValidation(CommandResultValidation.None)
            .WithToolExecutionLog(verbose)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
            throw new FlowlineException($"Failed to set solution version to {version}.");
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
    public string? PublisherPrefix { get; set; }
    public string? VersionNumber { get; set; }
    public bool IsManaged { get; set; }
}
