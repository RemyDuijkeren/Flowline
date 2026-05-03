using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Models;

public record DataverseSolutionInfo(
    Guid Id,
    string UniqueName,
    string PublisherPrefix,
    bool IsManaged,
    EntityReference? ParentSolution);
