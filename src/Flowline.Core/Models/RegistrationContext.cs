using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Models;

public sealed record RegistrationContext(
    IOrganizationServiceAsync2 Service,
    PluginAssemblyMetadata Metadata,
    Entity Assembly,
    string SolutionName,
    bool Save,
    CancellationToken CancellationToken);
