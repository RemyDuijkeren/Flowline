using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Models;

public sealed record RegistrationSnapshot(
    IReadOnlyDictionary<string, Entity> PluginTypes,
    IReadOnlyList<Entity> Steps,
    IReadOnlyList<Entity> Images,
    IReadOnlyList<Entity> CustomApis,
    IReadOnlyList<Entity> RequestParams,
    IReadOnlyList<Entity> ResponseProps,
    IReadOnlyDictionary<string, Guid> SdkMessageIds,
    IReadOnlyDictionary<(Guid MessageId, string EntityName, string? SecondaryEntity), Guid?> FilterIds,
    string PublisherPrefix
);
