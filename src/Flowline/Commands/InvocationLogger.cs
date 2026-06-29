using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Microsoft.Extensions.Logging;

namespace Flowline.Commands;

internal static class InvocationLogger
{
    internal static string HashUrl(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant();

    internal static void Log(ILogger logger, FlowlineRuntimeOptions runtimeOptions, ProjectConfig? config, string rootFolder, Activity? activity)
    {
        var tv = runtimeOptions.ToolVersions;
        if (tv is null) return;

        var cfg = config ?? new ProjectConfig();
        var ciPlatform = ConsoleHelper.DetectCIPlatform();
        var ci = ciPlatform is not null;

        var envTiers = new List<string>(4);
        var envHashes = new List<string>(4);
        foreach (var (tier, url) in new[] { ("prod", cfg.ProdUrl), ("uat", cfg.UatUrl), ("test", cfg.TestUrl), ("dev", cfg.DevUrl) })
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                envTiers.Add(tier);
                envHashes.Add($"{tier}={HashUrl(url)}");
            }
        }

        var solutionNames = string.Join(",", cfg.Solutions.Select(s => s.Name));
        var envConfigured = string.Join(",", envTiers);
        var envHashStr = string.Join(",", envHashes);

        logger.LogInformation(
            "Invocation: {FlowlineVersion} dotnet={DotNetVersion} pac={PacVersion}({PacInstallType}) git={GitVersion}@{GitBranch} os={Os} arch={OsArch} ci={Ci} ci.platform={CiPlatform} verbose={Verbose} force={Force} root={ProjectRoot} solutions={ProjectSolutions} env.configured={EnvConfigured} env.hashes={EnvHashes}",
            tv.FlowlineVersion, tv.DotNetVersion, tv.PacVersion, tv.PacInstallType,
            tv.GitVersion, tv.GitBranch,
            RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture,
            ci, ciPlatform,
            runtimeOptions.IsVerbose, runtimeOptions.Force,
            rootFolder, solutionNames, envConfigured, envHashStr);

        if (activity is null) return;
        activity.SetTag("flowline.version", tv.FlowlineVersion);
        activity.SetTag("dotnet.version", tv.DotNetVersion);
        activity.SetTag("pac.version", tv.PacVersion);
        activity.SetTag("pac.installType", tv.PacInstallType);
        activity.SetTag("git.version", tv.GitVersion);
        activity.SetTag("git.branch", tv.GitBranch);
        activity.SetTag("os", RuntimeInformation.OSDescription);
        activity.SetTag("os.arch", RuntimeInformation.OSArchitecture.ToString());
        activity.SetTag("ci", ci);
        activity.SetTag("ci.platform", ciPlatform);
        activity.SetTag("verbose", runtimeOptions.IsVerbose);
        activity.SetTag("force", runtimeOptions.Force);
        activity.SetTag("project.root", rootFolder);
        activity.SetTag("project.solutions", solutionNames);
        activity.SetTag("env.configured", envConfigured);
        activity.SetTag("env.hashes", envHashStr);
    }
}
