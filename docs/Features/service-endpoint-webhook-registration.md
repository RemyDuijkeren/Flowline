# Change Request: Service Endpoint and Webhook Registration

## Problem

Flowline can register plugin-backed `sdkmessageprocessingstep` records, but it cannot register
Dataverse service endpoints or webhook-backed steps.

Dataverse supports this natively:

- A webhook is a `serviceendpoint` record with `contract = Webhook`.
- Service Bus, Event Hub, Event Grid, Queue, Topic, and related targets are also represented by
  `serviceendpoint` records with different `contract` values.
- A service endpoint step is an `sdkmessageprocessingstep` where the event handler is a
  `serviceendpoint` instead of a `plugintype`.

The current Flowline registration model assumes every step is plugin-backed. This prevents teams
from using Flowline as the source of truth for integrations where Dataverse should directly post
the execution context to an external endpoint.

## Goal

Add attribute-driven registration for:

1. Reusable service endpoint definitions.
2. Webhook definitions as a first-class convenience over `serviceendpoint`.
3. Endpoint-backed message processing steps.
4. Optional assembly-level endpoint definitions for small/shared infrastructure endpoints.

The primary API should use marker classes. Assembly attributes are supported as an additional
option, not the main documented path.

## Non-goals

- Do not generate custom payloads. A native endpoint step sends the Dataverse remote execution
  context. For custom JSON payloads, use a normal `[Step]` plugin and send the payload from code.
- Do not store endpoint secrets directly in source-controlled attributes.
- Do not replace existing plugin step registration.
- Do not support endpoint images until the first endpoint step implementation is stable. Images can
  be added later because `sdkmessageprocessingstepimage` is tied to the step regardless of handler
  type.

## Recommended User Model

### Option 3: Marker class endpoint definitions

Marker classes are the primary API. They make endpoints visible in the project tree, easy to search,
easy to review, and easy to colocate with related endpoint steps.

```csharp
using Flowline.Attributes;

[WebhookEndpoint("av_AccountWebhook",
    UrlEnvVar = "ACCOUNT_WEBHOOK_URL",
    AuthValueEnvVar = "ACCOUNT_WEBHOOK_KEY",
    Description = "Sends account update events to the integration platform.")]
public sealed class AccountWebhookEndpoint;

[EndpointStep("av_AccountWebhook", "account")]
[Filter("name", "telephone1")]
public sealed class AccountPostUpdateAsyncWebhook;
```

Class name parsing follows the same convention as `[Step]`:

```text
{DescriptiveName}{Stage}{Message}[Async][Webhook|Endpoint]
```

Examples:

| Class name | Message | Stage | Mode |
|---|---|---|---|
| `AccountPostUpdateAsyncWebhook` | Update | PostOperation | Asynchronous |
| `AccountPostCreateEndpoint` | Create | PostOperation | Synchronous |
| `OrderValidationDeleteWebhook` | Delete | PreValidation | Synchronous |

### Option 4: Assembly-level endpoint definitions

Assembly attributes are supported for shared infrastructure endpoints where a marker class would add
noise. This is the secondary API.

```csharp
using Flowline.Attributes;

[assembly: WebhookEndpoint("av_DefaultIntegrationWebhook",
    UrlEnvVar = "DEFAULT_WEBHOOK_URL",
    AuthValueEnvVar = "DEFAULT_WEBHOOK_KEY")]

[EndpointStep("av_DefaultIntegrationWebhook", "account")]
[Filter("name")]
public sealed class AccountPostUpdateAsyncWebhook;
```

Flowline should treat class-level and assembly-level endpoint definitions identically after
discovery. If the same endpoint name is declared more than once, fail the push. Duplicate endpoint
definitions are ambiguous and should not be merged.

## Attribute API

### `WebhookEndpointAttribute`

Webhook-specific convenience wrapper for `serviceendpoint` with `contract = Webhook`.

```csharp
namespace Flowline.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class WebhookEndpointAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public string? Url { get; set; }
    public string? UrlEnvVar { get; set; }

    public ServiceEndpointAuthType AuthType { get; set; } = ServiceEndpointAuthType.WebhookKey;
    public string? AuthValueEnvVar { get; set; }

    public ServiceEndpointMessageFormat MessageFormat { get; set; } = ServiceEndpointMessageFormat.Json;
    public string? Description { get; set; }
}
```

Validation:

- `Name` is required and must not be empty.
- Exactly one of `Url` and `UrlEnvVar` must be set.
- `AuthValue` should not exist as an attribute property. Only `AuthValueEnvVar` is supported for
  secrets.
- `AuthType` defaults to `WebhookKey`.
- `MessageFormat` defaults to `Json`.

### `ServiceEndpointAttribute`

Generic service endpoint definition for Service Bus, Event Hub, Event Grid, Queue, Topic, Webhook,
and future contracts.

```csharp
namespace Flowline.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ServiceEndpointAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public ServiceEndpointContract Contract { get; set; }
    public ServiceEndpointAuthType AuthType { get; set; }
    public ServiceEndpointMessageFormat MessageFormat { get; set; } = ServiceEndpointMessageFormat.Json;

    public string? Url { get; set; }
    public string? UrlEnvVar { get; set; }

    public string? NamespaceAddress { get; set; }
    public string? NamespaceAddressEnvVar { get; set; }
    public string? Path { get; set; }

    public string? AuthValueEnvVar { get; set; }
    public string? SASKeyNameEnvVar { get; set; }
    public string? SASKeyEnvVar { get; set; }
    public string? SASTokenEnvVar { get; set; }

    public string? Description { get; set; }
}
```

Validation:

- `Name` is required and must not be empty.
- `Contract` is required. `Unknown` or default `0` is invalid.
- For `Webhook`, exactly one of `Url` and `UrlEnvVar` must be set.
- For Service Bus style contracts, endpoint addressing must be explicit enough to create a valid
  `serviceendpoint`: `NamespaceAddress` or `NamespaceAddressEnvVar`, plus `Path` when required by
  the contract.
- Secret values are never stored directly in attributes. Use environment variable names.
- If an endpoint references an environment variable and the variable is missing during `flowline push`,
  fail with a clear error naming the endpoint and missing variable.

### `EndpointStepAttribute`

Defines a service endpoint-backed message processing step.

```csharp
namespace Flowline.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public sealed class EndpointStepAttribute(string endpointName, string? entity = null) : Attribute
{
    public string EndpointName { get; } = endpointName;
    public string? Entity { get; } = entity;

    public int Order { get; set; } = 1;
    public bool DeleteJobOnSuccess { get; set; } = true;
}
```

Rules:

- `EndpointName` must match a discovered `WebhookEndpoint` or `ServiceEndpoint`.
- `Entity` follows the same rules as `[Step]`: logical name, `null` for accidental global with
  warning, `"none"` for intentional global.
- Class name parsing determines message, stage, and sync/async mode.
- `[Filter]` works the same as plugin-backed steps and is valid only for Update.
- `[SecondaryEntity]` works the same as plugin-backed steps and applies to Associate/Disassociate.
- Do not allow `[Step]` and `[EndpointStep]` on the same class.
- Do not require endpoint step marker classes to implement `IPlugin`.

## Enums

Add these enums to `Flowline.Attributes`.

```csharp
public enum ServiceEndpointContract
{
    OneWay = 1,
    Queue = 2,
    Rest = 3,
    TwoWay = 4,
    Topic = 5,
    QueuePersistent = 6,
    EventHub = 7,
    Webhook = 8,
    EventGrid = 9,
    ManagedDataLake = 10,
    ContainerStorage = 11
}

public enum ServiceEndpointAuthType
{
    NotSpecified = 0,
    ACS = 1,
    SASKey = 2,
    SASToken = 3,
    WebhookKey = 4,
    HttpHeader = 5,
    HttpQueryString = 6,
    ConnectionString = 7,
    AccessKey = 8,
    ManagedIdentity = 9
}

public enum ServiceEndpointMessageFormat
{
    BinaryXml = 1,
    Json = 2,
    TextXml = 3
}
```

## Dataverse Field Mapping

### `serviceendpoint`

| Attribute model | Dataverse field |
|---|---|
| `Name` | `name` |
| `Description` | `description` |
| `Contract` | `contract` |
| `AuthType` | `authtype` |
| `MessageFormat` | `messageformat` |
| `Url` / resolved `UrlEnvVar` | `url` |
| `NamespaceAddress` / resolved `NamespaceAddressEnvVar` | `namespaceaddress` |
| `Path` | `path` |
| resolved `AuthValueEnvVar` | `authvalue` |
| resolved `SASKeyNameEnvVar` | `saskeyname` |
| resolved `SASKeyEnvVar` | `saskey` |
| resolved `SASTokenEnvVar` | `sastoken` |

Use `CreateRequest` with `SolutionUniqueName` when creating, the same pattern used for plugin
assemblies and web resources.

Some secret fields are write-only or not valid for read. The planner cannot reliably diff secret
values. It should:

- Write secrets on create when the relevant env var is present.
- On update, write secret fields only when a corresponding env var is present.
- Never clear a secret because an env var is missing. Missing env vars should fail validation before
  planning if the attribute requires them.

### `sdkmessageprocessingstep`

For endpoint-backed steps, use `eventhandler` instead of `plugintypeid`.

| Step model | Dataverse field |
|---|---|
| generated step name | `name` |
| endpoint id | `eventhandler = EntityReference("serviceendpoint", id)` |
| message id | `sdkmessageid` |
| message filter id | `sdkmessagefilterid` |
| stage | `stage` |
| sync/async mode | `mode` |
| `Order` | `rank` |
| `[Filter]` | `filteringattributes` |
| async post operation | `asyncautodelete` from `DeleteJobOnSuccess` |
| server only | `supporteddeployment = 0` |
| Flowline marker | `description` |

Do not set `plugintypeid` for endpoint-backed steps.

## Discovery and Models

Extend assembly analysis to discover:

- Class-level `WebhookEndpointAttribute`.
- Assembly-level `WebhookEndpointAttribute`.
- Class-level `ServiceEndpointAttribute`.
- Assembly-level `ServiceEndpointAttribute`.
- Class-level `EndpointStepAttribute`.

Suggested model records:

```csharp
public sealed record ServiceEndpointMetadata(
    string Name,
    ServiceEndpointContract Contract,
    ServiceEndpointAuthType AuthType,
    ServiceEndpointMessageFormat MessageFormat,
    string? Url,
    string? UrlEnvVar,
    string? NamespaceAddress,
    string? NamespaceAddressEnvVar,
    string? Path,
    string? AuthValueEnvVar,
    string? SASKeyNameEnvVar,
    string? SASKeyEnvVar,
    string? SASTokenEnvVar,
    string? Description,
    string Source);

public sealed record EndpointStepMetadata(
    string EndpointName,
    string TypeName,
    string Name,
    string? Entity,
    MessageName Message,
    ExecutionStage Stage,
    ExecutionMode Mode,
    int Order,
    bool DeleteJobOnSuccess,
    IReadOnlyList<string> FilteringAttributes,
    string? SecondaryEntity);
```

`Source` should be a friendly source string for diagnostics, for example:

- `class AccountWebhookEndpoint`
- `assembly attribute WebhookEndpoint("av_DefaultIntegrationWebhook")`

## Reader Changes

Extend `PluginReader` or add a dedicated `EndpointRegistrationReader`.

Read existing `serviceendpoint` records for the solution:

- Join `serviceendpoint` to `solutioncomponent` by `serviceendpointid = objectid`.
- Component type is `95`.
- Fetch fields needed for diffing: `serviceendpointid`, `name`, `description`, `contract`,
  `authtype`, `messageformat`, `url`, `namespaceaddress`, `path`, `saskeyname`.
- Do not expect secret fields such as `authvalue`, `saskey`, or `sastoken` to be readable.

Read endpoint-backed `sdkmessageprocessingstep` records:

- Query `sdkmessageprocessingstep`.
- Include `eventhandler`, `sdkmessageid`, `sdkmessagefilterid`, `stage`, `mode`, `rank`,
  `filteringattributes`, `asyncautodelete`, `supporteddeployment`, `description`.
- Filter or classify records where `eventhandler.LogicalName == "serviceendpoint"`.
- Keep plugin-backed and endpoint-backed steps separate in the snapshot to avoid accidental deletes.

## Planning Changes

Add endpoint planning as a new registration domain next to plugin types, steps, images, and Custom
APIs.

Planning order:

1. Validate endpoint definitions and endpoint steps.
2. Plan service endpoint creates, updates, and deletes.
3. Resolve local endpoint names to Dataverse endpoint ids. For new endpoints, reserve a GUID in the
   planned entity so dependent step plans can reference it.
4. Plan endpoint step creates, updates, and deletes.
5. Execute deletes in dependency order.
6. Execute endpoint creates/updates.
7. Execute endpoint step creates/updates.

Delete order:

1. Endpoint-backed step images, if endpoint images are supported later.
2. Endpoint-backed steps.
3. Service endpoints.

Do not let endpoint planning delete plugin-backed steps, and do not let plugin step planning delete
endpoint-backed steps.

## Matching Keys

### Service endpoints

Match endpoints by `name`.

The name is the stable source-of-truth key. It should usually include the publisher prefix or another
organization-level prefix:

```csharp
[WebhookEndpoint("av_AccountWebhook", ...)]
```

### Endpoint steps

Use the same generated step naming strategy as plugin steps, but include the endpoint name in the
name to avoid collisions:

```text
{EndpointName}: {Message} {EntityOrNone} {Stage} {Mode}
```

Example:

```text
av_AccountWebhook: Update account PostOperation Async
```

Match endpoint steps by name. Existing Flowline plugin-backed steps should keep their current naming
strategy.

## Validation Rules

Fail `flowline push` before making changes when:

- Two endpoint definitions have the same `Name`.
- `[EndpointStep]` references an unknown endpoint.
- A class has both `[Step]` and `[EndpointStep]`.
- An endpoint step marker class also implements `IPlugin`. This is confusing and should be split
  into either a plugin step or endpoint step.
- Endpoint name is empty.
- Required environment variables are missing.
- `[Filter]` is used on a non-Update endpoint step.
- `[SecondaryEntity]` is missing on Associate/Disassociate endpoint steps; warn first if the same
  behavior is used for plugin steps today, then align with the existing plugin validation behavior.
- `[EndpointStep]` class name cannot be parsed into message/stage/mode.
- `DeleteJobOnSuccess` is explicitly set on a synchronous step. Use the same warning/error behavior
  as plugin-backed steps.

## Lifecycle

Flowline treats endpoint attributes as source of truth:

- Create `serviceendpoint` when declared locally and missing in Dataverse.
- Update mutable endpoint fields when they differ.
- Delete Flowline-owned service endpoints when removed from code, unless `--save` is used.
- Create endpoint-backed steps when declared locally and missing in Dataverse.
- Update endpoint-backed steps when mutable fields differ.
- Delete endpoint-backed steps when removed from code, unless `--save` is used.

Only delete records stamped with the Flowline marker in `description`. This avoids deleting manually
registered endpoints that happen to share the solution.

Endpoint delete safety:

- If a service endpoint still has non-Flowline endpoint steps attached, do not delete it.
- If a service endpoint appears in more than one non-default unmanaged solution, follow the same
  future smart orphan behavior planned for web resources: remove from this solution or skip, but do
  not blindly delete.

## CLI Output

Add concise output in the same style as plugin registration:

```text
Creating service endpoint av_AccountWebhook
Updating service endpoint av_AccountWebhook
Creating endpoint step av_AccountWebhook: Update account PostOperation Async
Deleting endpoint step av_AccountWebhook: Update account PostOperation Async
```

Warnings should identify both the endpoint/step name and the source:

```text
Endpoint av_AccountWebhook in class AccountWebhookEndpoint references missing env var ACCOUNT_WEBHOOK_URL.
```

## Documentation Updates

Update `src/Flowline.Attributes/README.md` with:

- "Service endpoints and webhooks" section.
- Marker-class examples.
- Assembly-level examples as an additional option.
- Explanation that native endpoint steps send Dataverse execution context.
- Explanation that custom JSON payloads require a normal plugin step.
- Secret handling guidance.

## Tests

Add unit tests for:

1. Discovering class-level `WebhookEndpoint`.
2. Discovering assembly-level `WebhookEndpoint`.
3. Discovering class-level `ServiceEndpoint`.
4. Duplicate endpoint names fail.
5. Unknown endpoint reference from `[EndpointStep]` fails.
6. Missing required env var fails with endpoint name and env var name.
7. Webhook endpoint maps to `serviceendpoint.contract = 8`.
8. Endpoint step creates `sdkmessageprocessingstep.eventhandler`, not `plugintypeid`.
9. Endpoint step uses message/filter/stage/mode/order/filtering attributes correctly.
10. `[Filter]` on non-Update endpoint step fails.
11. `[Step]` and `[EndpointStep]` on same class fails.
12. `--save` suppresses endpoint and endpoint step deletes.
13. Plugin-backed step planning does not delete endpoint-backed steps.
14. Endpoint-backed step planning does not delete plugin-backed steps.
15. Secret fields are written on create when env vars are present.
16. Secret fields are not diffed from retrieved Dataverse values.

## Implementation Notes

Prefer implementing this as a separate endpoint registration sub-domain rather than folding it into
the existing plugin step code path. The Dataverse table is the same for steps, but the handler model
is different enough that separate models and planning logic will be easier to verify:

- Plugin steps use `plugintypeid`.
- Endpoint steps use `eventhandler`.
- Plugin type lifecycle and endpoint lifecycle are independent.

The executor can still reuse shared helpers for create/update/delete and solution association.

