---
title: "Packed solution zips carry solution.xml at the root, not Other/Solution.xml"
date: 2026-08-09
category: docs/solutions/logic-errors/
module: flowline-cli
problem_type: logic_error
component: tooling
severity: high
symptoms:
  - "deploy --path <zip> fails with: No Other/Solution.xml entry found in artifact '<path>' — is this a valid packed solution zip?"
  - "Every real packed artifact is rejected, including one Flowline packed itself into artifacts/"
  - "The zip opens fine and pac imports it, so the artifact is not actually corrupt"
root_cause: wrong_api
resolution_type: code_fix
related_components:
  - DeployCommand
  - MissingComponentCheckService
tags:
  - solution-zip
  - pac-cli
  - deploy
  - artifact
---

# Packed solution zips carry solution.xml at the root, not Other/Solution.xml

## Problem

`flowline deploy --path <zip>` could never read a solution manifest. It looked for an
`Other/Solution.xml` entry inside the artifact, but that entry only exists in the *unpacked source*
layout — a packed solution zip carries `solution.xml` at its root. Every genuine packed artifact was
rejected as invalid.

(Paths like `Other/Solution.xml` and `Solution/src/` below are entries inside a solution zip, or
inside a user's scaffolded Flowline project per `docs/folder-structure.md` — not files in this
repository.)

## Symptoms

```
Error: No Other/Solution.xml entry found in artifact
       'C:\...\artifacts\FlowlineDeployTest_unmanaged.zip' — is this a valid packed solution zip?
```

The message is misleading in a way that costs time: it blames the artifact, and the artifact is
fine. The same zip imports correctly through `pac solution import`.

## What Didn't Work

**Assuming the artifact was stale or corrupt.** The obvious first read is that the zip was built
wrong. It wasn't — Flowline had packed it itself minutes earlier.

**Reasoning about it from the unpacked source tree.** In a Flowline project, the manifest you edit,
commit, and diff sits at `Solution/src/Other/Solution.xml`, so that is the one that comes to mind
when you think "the solution's manifest". That familiarity is the whole trap. The committed path and the packed path are different
files in different layouts, and only the unpacked one lives at `Other/`.

The bug survived because nothing exercised it: the packed route never calls this function, and no
test built a zip in the packed shape — every existing test fixture created `Other/Solution.xml`,
so the tests agreed with the bug.

## Solution

Listing the actual entries settles it immediately:

```powershell
$z = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$z.Entries | ForEach-Object { $_.FullName }
```

For a packed unmanaged solution:

```
customizations.xml
solution.xml
WebResources/av_...
pluginpackages/av_....Plugins/pluginpackage.xml
[Content_Types].xml
```

No `Other/` anywhere. The fix accepts the packed layout first and keeps the unpacked one as a
fallback, matching case-insensitively — `ZipArchive.GetEntry` is an exact string match, and casing
varies by producer:

```csharp
internal static ZipArchiveEntry? FindSolutionManifestEntry(ZipArchive archive) =>
    archive.Entries.FirstOrDefault(e => e.FullName.Equals("solution.xml", StringComparison.OrdinalIgnoreCase))
    ?? archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("Other/Solution.xml", StringComparison.OrdinalIgnoreCase));
```

`ReadArtifactSolutionManifest` in `src/Flowline/Commands/DeployCommand.cs` calls this instead of
`archive.GetEntry("Other/Solution.xml")`, and the "not found" message no longer names a path that a
packed zip was never going to contain.

Fixed on branch `feat/deploy-import-preflight` — unmerged as of this writing.

## Why This Works

There are two distinct layouts and they are easy to conflate:

| Layout | Produced by | Manifest lives at |
|---|---|---|
| **Packed zip** | `pac solution pack` / `flowline deploy` | `solution.xml` at the zip root |
| **Unpacked source** | `pac solution unpack` into `Solution/src/`, committed to git | `Other/Solution.xml` |

`--path` is handed the *packed* artifact, so the root layout is the one that applies. The fallback
costs nothing and covers a zipped-up source tree, which is a plausible thing for someone to pass.

The same distinction governs anything else that reads inside a solution zip. Dataverse's
`RetrieveMissingComponents` message reads the required-component list out of the packed zip's root
`solution.xml`, which is why the missing-component preflight gate works on the artifact Flowline
packs rather than needing a live export.

## Prevention

**Verify the layout by listing entries before coding against a path inside a solution zip.** Two
lines of PowerShell settles a question that otherwise gets answered from memory of the source tree.

**Target the root `solution.xml` when reading a packed artifact.** Reach for `Other/Solution.xml`
only when the input is genuinely an unpacked tree or a zip of one.

**Match zip entry names case-insensitively.** `ZipArchive.GetEntry` is an exact string match, so a
producer that writes `Solution.xml` silently misses a lookup for `solution.xml`.

**Build test fixtures in the shape the production input actually has.** The pre-existing tests all
created `Other/Solution.xml`, so they confirmed the bug rather than catching it. The regression
tests now cover the packed layout, the unpacked layout, mixed casing, and a zip containing both
(root wins):

```csharp
[Fact]
public void ReadArtifactSolutionManifest_ReadsPackedLayout_SolutionXmlAtZipRoot()
{
    using var tmp = new TempArtifactZip(zip => WriteManifest(zip, "solution.xml", "3.1.4.1", managed: "0"));

    var result = DeployCommand.ReadArtifactSolutionManifest(tmp.ZipPath);

    result.Version.Should().Be("3.1.4.1");
}
```

**Treat "is this a valid X?" error text as a hypothesis, not a diagnosis.** Here the message accused
the artifact and the artifact was correct. An error that names what it looked for (`solution.xml`)
rather than only what it concluded gives the next reader a faster path to the real answer.

## Related

- `docs/solutions/design-patterns/pac-solution-xml-diff-pattern.md` — working with the unpacked
  `Other/Solution.xml` in source control
- `docs/solutions/architecture-patterns/post-deploy-service-di-fanout-protocol.md` — the pre-import
  service protocol whose `PackagePath` is the packed artifact this layout applies to
