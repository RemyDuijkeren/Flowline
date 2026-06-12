---
title: Service Endpoint and Webhook Registration
date: 2026-06-12
status: idea
origin: docs/Features/service-endpoint-webhook-registration.md
---

# Service Endpoint and Webhook Registration

## Problem

Flowline only registers plugin-backed steps. It cannot register Dataverse service endpoints or
webhook-backed steps. Teams using Flowline as source of truth for integrations where Dataverse
should post directly to an external endpoint must manage those registrations manually.

---

## User Model

### Marker Classes (primary API)

```csharp
[WebhookEndpoint("av_AccountWebhook",
    UrlEnvVar = "ACCOUNT_WEBHOOK_URL",
    AuthValueEnvVar = "ACCOUNT_WEBHOOK_KEY")]
public sealed class AccountWebhookEndpoint;

[EndpointStep("av_AccountWebhook", "account")]
[Filter("name", "telephone1")]
public sealed class AccountPostUpdateAsyncWebhook;
```

Class name parsing (same convention as `[Step]`): `{Name}{Stage}{Message}[Async][Webhook|Endpoint]`

### Assembly Attributes (secondary API)

```csharp
[assembly: WebhookEndpoint("av_DefaultIntegrationWebhook",
    UrlEnvVar = "DEFAULT_WEBHOOK_URL",
    AuthValueEnvVar = "DEFAULT_WEBHOOK_KEY")]
```

Same behaviour as class-level after discovery. Duplicate endpoint names → fail push.

---

## Attributes

### `WebhookEndpointAttribute`

Convenience wrapper for `serviceendpoint` with `contract = Webhook`.

- `Name` (required)
- `Url` or `UrlEnvVar` (exactly one required)
- `AuthType` (default `WebhookKey`)
- `AuthValueEnvVar` (secret — never `AuthValue` directly)
- `MessageFormat` (default `Json`)
- `Description`

### `ServiceEndpointAttribute`

Generic for Service Bus, Event Hub, Event Grid, Queue, Topic, Webhook, etc.

- All `WebhookEndpoint` fields plus `NamespaceAddress`/`NamespaceAddressEnvVar`, `Path`,
  `SASKeyNameEnvVar`, `SASKeyEnvVar`, `SASTokenEnvVar`
- `Contract` (required, must not be default `0`)

### `EndpointStepAttribute`

```csharp
[EndpointStep("av_AccountWebhook", "account")]
```

- `EndpointName` must match a discovered endpoint
- `Entity` follows same rules as `[Step]`
- `[Filter]` valid on Update steps only
- `[SecondaryEntity]` for Associate/Disassociate
- Cannot combine with `[Step]`; class must not implement `IPlugin`
- `Order` (default 1), `DeleteJobOnSuccess` (default true)

---

## Dataverse Mapping

Service endpoints → `serviceendpoint` table (component type `95`).
Endpoint steps → `sdkmessageprocessingstep` using `eventhandler = EntityReference("serviceendpoint", id)`
instead of `plugintypeid`.

Step name format: `{EndpointName}: {Message} {Entity} {Stage} {Mode}`

---

## Enums to Add to `Flowline.Attributes`

`ServiceEndpointContract` (OneWay=1 … ContainerStorage=11), `ServiceEndpointAuthType`
(NotSpecified=0 … ManagedIdentity=9), `ServiceEndpointMessageFormat` (BinaryXml=1, Json=2, TextXml=3).

---

## Key Design Points

- Secrets: write on create when env var present; on update write only if env var present; never clear
  a secret because env var is missing
- Plan ordering: validate → endpoint creates/updates → resolve endpoint IDs → step creates/updates →
  deletes in reverse dependency order
- Delete safety: only delete records with Flowline marker in `description`; don't delete endpoint if
  it has non-Flowline steps still attached
- Plugin-backed step planning must not delete endpoint-backed steps and vice versa
- Separate sub-domain from plugin steps (different handler model: `plugintypeid` vs `eventhandler`)

---

## Validation (fail push before any changes)

- Duplicate endpoint names
- `[EndpointStep]` referencing unknown endpoint
- `[Step]` + `[EndpointStep]` on same class
- `[EndpointStep]` class also implements `IPlugin`
- Missing required env vars (name the endpoint and variable in error)
- `[Filter]` on non-Update endpoint step
- Unparseable class name

---

## Out of Scope

- Custom payload generation (use a normal plugin step for that)
- Endpoint step images (add later once basic path is stable)
- Storing secrets in source attributes
