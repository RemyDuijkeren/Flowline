using Flowline.Core.Validation;
using Flowline.Diagnostics;
using Flowline.Utils;

namespace Flowline.Infrastructure;

// KTD3: binds ValidationProbes to the real pac, git and dotnet processes. The probes themselves only
// declare the delegates, so the engine never names a subprocess helper.
public static class PacValidationProbes
{
    public static ValidationProbes Create(SubprocessCapture capture) => new()
    {
        CheckDotNetAsync = DotNetUtils.AssertDotNetInstalledAsync,
        CheckPacAsync = PacUtils.AssertPacCliInstalledAsync,
        CheckGitAsync = (verbose, ct) => GitUtils.AssertGitInstalledAsync(capture, verbose, ct),
        CheckGitRepoAsync = (rootFolder, verbose, ct) => GitUtils.AssertGitRepoAsync(rootFolder, capture, verbose, ct),
        GetEnvironmentAsync = (url, ct) => PacUtils.GetEnvironmentInfoByUrlAsync(url, capture, ct),
        GetSolutionsAsync = (url, ct) => PacUtils.GetSolutionsAsync(url, capture, ct),
        GetPublisherCustomizationPrefixAsync = (url, name, ct) => PacUtils.GetPublisherCustomizationPrefixAsync(url, name, capture, ct),
    };
}
