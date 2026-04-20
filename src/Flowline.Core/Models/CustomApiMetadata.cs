namespace Flowline.Core.Models;

public record CustomApiMetadata(
    string UniqueName,              // PascalCase, without publisher prefix — e.g. "GetAccountRisk"
    string? DisplayName,
    string? Description,
    int BindingType,                // 0=Global, 1=Entity, 2=EntityCollection
    string? BoundEntityLogicalName,
    bool IsFunction,
    bool IsPrivate,
    int AllowedStepType,
    string? ExecutePrivilege,
    string PluginTypeFullName,
    List<CustomApiRequestParameterMetadata> RequestParameters,
    List<CustomApiResponsePropertyMetadata> ResponseProperties);
