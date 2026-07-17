using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Generators;

public record GenerationContext(
    IOrganizationServiceAsync2 Service,
    SolutionInfo RemoteSolution,
    string SolutionName,
    string DevUrl,
    string ModelNamespace,
    string[] ExtraTables,
    string TempOutputPath,
    XrmContextAuth? XrmContextAuth,
    bool Verbose,
    string OutputLabel,
    string? ServiceContextName = null,
    PacProfile? ResolvedProfile = null,
    string? ResolvedSecret = null
);
