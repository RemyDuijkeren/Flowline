using CliWrap;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public abstract record XrmContextAuth
{
    public sealed record ConnectionString(string Value) : XrmContextAuth;
    public sealed record ClientSecret(string ClientId, string Secret) : XrmContextAuth;
    /// <summary>Browser OAuth via XrmContext's native /method:OAuth — single auth context,
    /// single browser window after first login.</summary>
    public sealed record BrowserOAuth(string? AppId = null) : XrmContextAuth;
}

public class XrmContextRunner(IAnsiConsole console, SubprocessCapture? capture = null)
{
    public virtual async Task RunAsync(
        string exePath,
        string environmentUrl,
        XrmContextAuth auth,
        string solutionName,
        string[]? extraTables,
        string modelNamespace,
        string tempOutputPath,
        string serviceContextName = "XrmContext",
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgs(environmentUrl, auth, solutionName, extraTables, modelNamespace, tempOutputPath, serviceContextName);

        // XrmContext does not create the output directory itself — it fails if it doesn't exist
        Directory.CreateDirectory(tempOutputPath);

        console.Verbose($"XrmContext exe: {exePath}");

        var cmd = Cli.Wrap(exePath)
            .WithArguments(args);

        await console.Status().FlowlineSpinner().StartAsync(
            $"Generating early-bound types...",
            ctx => (capture?.Apply(cmd, ctx) ?? cmd).ExecuteAsync(cancellationToken).Task);

        console.Ok("Early-bound types generated");
    }

    internal static string[] BuildArgs(
        string environmentUrl,
        XrmContextAuth auth,
        string solutionName,
        string[]? extraTables,
        string modelNamespace,
        string tempOutputPath,
        string serviceContextName = "XrmContext")
    {
        var args = new List<string>();

        switch (auth)
        {
            case XrmContextAuth.ConnectionString cs:
                args.Add($"/url:{environmentUrl.TrimEnd('/')}");
                args.Add("/method:ConnectionString");
                args.Add($"/connectionString:{cs.Value}");
                break;
            case XrmContextAuth.ClientSecret clientSecret:
                args.Add($"/url:{environmentUrl.TrimEnd('/')}/XRMServices/2011/Organization.svc");
                args.Add("/method:ClientSecret");
                args.Add($"/mfaAppId:{clientSecret.ClientId}");
                args.Add($"/mfaClientSecret:{clientSecret.Secret}");
                break;
            case XrmContextAuth.BrowserOAuth browserOAuth:
                // method:OAuth creates a single CrmServiceClient with one ADAL auth context.
                // All entity metadata queries share that context — only one browser window opens.
                // tokenCacheStorePath is not supported in method:OAuth for XrmContext 3.0.1.
                args.Add($"/url:{environmentUrl.TrimEnd('/')}/XRMServices/2011/Organization.svc");
                args.Add("/method:OAuth");
                args.Add($"/mfaAppId:{browserOAuth.AppId ?? DataverseConnector.PacCliAppId}");
                args.Add("/mfaReturnUrl:http://localhost");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(auth));
        }

        // https://github.com/delegateas/XrmContext/wiki/Generate-Context#generation-arguments
        args.Add($"/solutions:{solutionName}");
        args.Add($"/namespace:{modelNamespace}");
        args.Add($"/out:{tempOutputPath}");
        args.Add("/oneFile:false");
        args.Add($"/servicecontextname:{serviceContextName}");
        args.Add("/deprecatedprefix:ZZ_");

        if (extraTables is { Length: > 0 })
            args.Add($"/entities:{string.Join(",", extraTables)}");

        return args.ToArray();
    }
}
