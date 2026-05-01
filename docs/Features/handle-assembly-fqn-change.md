# Change Request: Handle Assembly FQN Change (Delete + Recreate)

## Problem

When the assembly's fully qualified name (FQN) changes — specifically when the public key token
changes (e.g. project recreated from scratch with a new `.snk`) or when the major or minor
version number is bumped — Dataverse rejects the `UpdateRequest` with:

> `Plugin Assembly fully qualified name has changed from: [Extensions, 1, 0, neutral, df889c1cc53657b7]
> to: [Extensions, 1, 0, neutral, a4d07ffa42de325f]`

The current code in `GetOrRegisterAssemblyAsync` simply calls `UpdateAsync` on the assembly when
the hash differs. It does not detect FQN changes upfront, so it crashes.

Daxif and spkl have the same bug — neither tool handles this case.

**Rules per Microsoft docs** (https://learn.microsoft.com/en-us/power-apps/developer/data-platform/register-plug-in#assembly-versioning):

| Change | Allowed via Update? |
|---|---|
| Public key token changed | No — must delete + recreate |
| Major or minor version changed | No — must delete + recreate |
| Build or revision changed | Yes — update in place |

## Solution

Pre-check the FQN before attempting an update. If the PKT or major/minor version has changed,
delete the existing `pluginassembly` record and create a fresh one. The cascade on delete
automatically removes all `plugintype`, `sdkmessageprocessingstep`, `sdkmessageprocessingstepimage`,
and `customapi` child records. The normal snapshot → plan → create flow then recreates everything
from scratch.

No special casing is needed in the phases that follow — `LoadSnapshot` will find an empty
environment, `Plan` will see all local plugins as new, and `ExecuteUpserts` will create them all.

## Changes required

### 1. `src/Flowline.Core/Models/PluginAssemblyMetadata.cs`

Add a `PublicKeyToken` property (nullable string, null when assembly is unsigned):

```csharp
public record PluginAssemblyMetadata(
    string Name,
    string FullName,
    byte[] Content,
    string Hash,
    string Version,
    string? PublicKeyToken,       // ← ADD: hex string e.g. "df889c1cc53657b7", null if unsigned
    string Culture,               // ← ADD: typically "neutral" for plugin assemblies
    List<PluginTypeMetadata> Plugins);
```

### 2. `src/Flowline.Core/Services/AssemblyAnalysisService.cs`

In the `Analyze` method, extract the public key token from the loaded assembly name and pass it
to `PluginAssemblyMetadata`. Add this after `var assemblyName = assembly.GetName();`:

```csharp
var pktBytes = assemblyName.GetPublicKeyToken();
var publicKeyToken = pktBytes is { Length: > 0 }
    ? Convert.ToHexString(pktBytes).ToLowerInvariant()
    : null;

var culture = string.IsNullOrEmpty(assemblyName.CultureName) ? "neutral" : assemblyName.CultureName;
```

Update the return statement at the bottom of `Analyze`:

```csharp
return new PluginAssemblyMetadata(
    assemblyName.Name!,
    assemblyName.FullName,
    content,
    hash,
    assemblyName.Version!.ToString(),
    publicKeyToken,               // ← ADD
    culture,                      // ← ADD
    pluginTypes);
```

### 3. `src/Flowline.Core/Services/PluginRegistrationService.cs`

#### 3a. Extend the query in `GetOrRegisterAssemblyAsync`

Add `"publickeytoken"` and `"culture"` to the `ColumnSet`:

```csharp
ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(
    "pluginassemblyid", "name", "version", "publickeytoken", "culture", "description"),
```

#### 3b. Replace the final return in `GetOrRegisterAssemblyAsync`

Replace the current two-liner at the end of the method:

```csharp
// REMOVE:
await AddSolutionComponentAsync(service, existing.Id, solutionName, cancellationToken).ConfigureAwait(false);
var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
return (existing, storedHash != metadata.Hash);
```

With FQN change detection logic:

```csharp
// Check for FQN changes that Dataverse does not allow to be updated in place
var registeredPkt     = existing.GetAttributeValue<string>("publickeytoken");
var registeredCulture = existing.GetAttributeValue<string>("culture") ?? "neutral";
var registeredVersion = existing.GetAttributeValue<string>("version"); // e.g. "1.0.0.0"

bool pktChanged        = !string.Equals(registeredPkt, metadata.PublicKeyToken, StringComparison.OrdinalIgnoreCase);
bool cultureChanged    = !string.Equals(registeredCulture, metadata.Culture, StringComparison.OrdinalIgnoreCase);
bool majorMinorChanged = HasMajorOrMinorVersionChange(registeredVersion, metadata.Version);

if (pktChanged || cultureChanged || majorMinorChanged)
{
    var reasons = new List<string>();
    if (pktChanged)       reasons.Add($"public key token ({registeredPkt ?? "null"} → {metadata.PublicKeyToken ?? "null"})");
    if (cultureChanged)   reasons.Add($"culture ({registeredCulture} → {metadata.Culture})");
    if (majorMinorChanged) reasons.Add($"major/minor version ({registeredVersion} → {metadata.Version})");
    var reason = string.Join(", ", reasons);

    output.Info($"[yellow]Assembly '{metadata.Name}' FQN changed ({reason}) — deleting and recreating all plugin registrations.[/]");

    await service.DeleteAsync("pluginassembly", existing.Id, cancellationToken).ConfigureAwait(false);

    var freshEntity = new Entity("pluginassembly")
    {
        ["name"]          = metadata.Name,
        ["content"]       = Convert.ToBase64String(metadata.Content),
        ["version"]       = metadata.Version,
        ["isolationmode"] = new OptionSetValue(2),
        ["description"]   = $"{FlowlineMarker} sha256={metadata.Hash}"
    };

    var response = (CreateResponse)await service.ExecuteAsync(
        new CreateRequest { Target = freshEntity, ["SolutionUniqueName"] = solutionName },
        cancellationToken).ConfigureAwait(false);

    freshEntity.Id = response.id;
    output.Info($"[green]Recreated assembly '{metadata.Name}'.[/]");
    return (freshEntity, false); // false = content already uploaded, no Phase 5 update needed
}

await AddSolutionComponentAsync(service, existing.Id, solutionName, cancellationToken).ConfigureAwait(false);
var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
return (existing, storedHash != metadata.Hash);
```

#### 3c. Add helper method `HasMajorOrMinorVersionChange` to `PluginRegistrationService`

Add this private static method alongside `ParseStoredHash`:

```csharp
/// <summary>
/// Returns true if the major or minor component of the version has changed.
/// Build and revision changes are allowed as in-place updates by Dataverse.
/// </summary>
static bool HasMajorOrMinorVersionChange(string? registered, string local)
{
    if (string.IsNullOrWhiteSpace(registered)) return false;

    if (!Version.TryParse(registered, out var reg)) return false;
    if (!Version.TryParse(local, out var loc))      return false;

    return reg.Major != loc.Major || reg.Minor != loc.Minor;
}
```

## No changes needed elsewhere

The delete cascade handles the child cleanup. After `GetOrRegisterAssemblyAsync` returns the new
assembly entity (with `needsUpdate = false`), the rest of `SyncAsync` proceeds unchanged:

- `LoadSnapshot` finds an empty environment → all maps are empty
- `Plan` sees all local types/steps/images/customapis as new → full create plan
- `ExecuteDeletes` → nothing to delete
- Phase 5 assembly update is skipped (`needsUpdate = false`)
- `ExecuteUpserts` creates everything fresh

## Tests to add

In `PluginRegistrationServiceTests.cs` (or a new test class), add tests for:

1. **PKT changed** — existing assembly has a different `publickeytoken` → service deletes old, creates new, returns `needsUpdate = false`
2. **Culture changed** — e.g. `neutral` → `en` → same delete+create path (rare in practice, but part of FQN)
3. **Major version changed** — e.g. `1.0.0.0` → `2.0.0.0` → same delete+create path
4. **Minor version changed** — e.g. `1.0.0.0` → `1.1.0.0` → same delete+create path
5. **Build/revision changed** — e.g. `1.0.0.0` → `1.0.5.0` → normal update path, no delete
6. **PKT null (unsigned) to null** — no FQN change triggered
7. **Multiple FQN fields changed simultaneously** — reason string lists all changed fields
8. `HasMajorOrMinorVersionChange` unit tests directly for the version parsing edge cases (null, unparseable, all four version components)
