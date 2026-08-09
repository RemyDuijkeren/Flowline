---
title: "RetrieveMissingComponents has no size ceiling but costs seconds per megabyte"
date: 2026-08-09
category: docs/solutions/tooling-decisions/
module: flowline-cli
problem_type: tooling_decision
component: tooling
severity: medium
applies_when: "Deciding whether a Dataverse organization-service message that carries a whole solution inline is cheap enough to run on every deploy"
related_components:
  - MissingComponentCheckService
  - DeployCommand
tags:
  - dataverse-sdk
  - deploy
  - performance
  - measurement
---

# RetrieveMissingComponents has no size ceiling but costs seconds per megabyte

## Context

Flowline's missing-component preflight gate hands a whole [[Packed solution]] to the target
environment's `RetrieveMissingComponents` message before importing. The message takes the solution
inline as a single byte array, while the import it guards (`pac solution import`) stages large
solutions through chunked upload.

That asymmetry produced a plausible-sounding worry during review: above some size the check would be
rejected while the import it guards would succeed, so teams with large solutions would pin the skip
flag permanently and the gate would be dead exactly where it mattered most. A second assumption sat
underneath it — that cost would track the *number* of required components, since a solution with
12,786 of them was the expensive-looking case.

Both were wrong, and only measurement showed it.

## Guidance

**There is no ceiling up to 64.5 MB, and cost tracks payload size, not component count.** Measured
against a live environment by padding one real solution with an incompressible entry, so every
request carried an identical required-component list and only size varied:

| Payload | Duration | Result |
|---|---|---|
| 0.5 MB | 5.7 s | 0 missing |
| 1.5 MB | 6.0 s | 0 missing |
| 8.5 MB | 26.5 s | 0 missing |
| 32.5 MB | 111.7 s | 0 missing |
| 64.5 MB | 216.9 s | 0 missing |

Nothing was rejected at any size. Above roughly 8 MB the cost is close to linear — on the order of
seconds per megabyte — so a 64 MB solution adds over three and a half minutes to every deploy.

**Design around duration, not a limit.** A gate on this message is safe to keep on by default for
ordinary solutions and becomes a real tax on large ones. Two consequences worth carrying:

- Do not write a failure message claiming the payload "exceeded a size limit" — no such rejection
  was observed. Attribute a large-payload failure to duration and a probable client timeout.
- A multi-minute call with a static progress label reads as a hang. Put the payload size in the
  spinner label once the wait stops being incidental, so the user can tell slow from stuck.

**Vary one dimension at a time when characterizing a remote call.** The padding approach — same
required-component list, different archive size — is what separated the two candidate drivers. Had
the sizes come from genuinely different solutions, component count and payload size would have moved
together and the result would have been uninterpretable.

## Why This Matters

The review finding was framed as a correctness risk (a guard that becomes unusable) and the real
constraint is a cost risk (a guard that makes every promotion slower). Those call for different
responses. The correctness framing points at degrading to a warning so the deploy proceeds — which
would turn the gate into something that passes without checking, the failure mode a guard exists to
prevent. The cost framing points at making the wait legible and keeping the opt-out, which is what
shipped.

The component-count assumption mattered too: it had been written into the plan as the likely driver,
and a future reader optimizing the gate would have started in the wrong place.

## When to Apply

- Before assuming a Dataverse organization-service message that carries a file inline has a size
  ceiling — measure rather than infer one from how other APIs behave.
- When deciding whether a per-deploy check is cheap enough to be on by default.
- When a remote call's cost could plausibly be driven by more than one dimension of the input.

## Examples

Building the padded artifacts — incompressible content so the archive size tracks the requested size
instead of collapsing under deflate:

```powershell
$bytes = New-Object byte[] ($mb * 1024 * 1024)
(New-Object Random 1234).NextBytes($bytes)

$zip = [System.IO.Compression.ZipFile]::Open($dest, [System.IO.Compression.ZipArchiveMode]::Update)
$entry = $zip.CreateEntry("WebResources/av_bigpayload_$mb", [System.IO.Compression.CompressionLevel]::NoCompression)
$s = $entry.Open(); $s.Write($bytes, 0, $bytes.Length); $s.Dispose()
$zip.Dispose()
```

The message itself is a plain typed request — the whole archive goes in one field:

```csharp
var response = (RetrieveMissingComponentsResponse)await service.ExecuteAsync(
    new RetrieveMissingComponentsRequest { CustomizationFile = zipBytes }, ct);
```

## Caveats

These numbers come from one tenant, one region, and one solution shape, over a single run per size.
They establish the *shape* of the cost curve and the absence of a ceiling in that range — treat the
specific seconds as indicative, not as a benchmark. Untested: whether the check and
`pac solution import` diverge somewhere beyond 64.5 MB, and whether a large payload combined with a
much larger required-component list behaves differently, since the two were never varied
independently above 0.5 MB.

## Related

- `docs/solutions/logic-errors/packed-vs-unpacked-solution-zip-layout.md` — the archive layout this
  message reads its required-component list from
