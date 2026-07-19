---
title: Solution File Project Wiring - Plan
type: feat
date: 2026-07-19
topic: sln-add-slnx-support
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Solution File Project Wiring - Plan

## Goal Capsule

- **Objective:** Give users a supported way to wire a `.cdsproj` into a project's solution file, and move new projects to `.slnx` while continuing to read `.sln`.
- **Product authority:** This document. Product decisions were settled during brainstorm; planning owns implementation choices only.
- **Open blockers:** One. Whether R7 and the `sln` branch ship in v1.0 (2026-07-20) or the following minor — a release-sequencing call recorded in Outstanding Questions. The rename removal (KD3, R10, R11) is unblocked either way.

---

## Product Contract

### Summary

Add `flowline sln add <path>`, a command that wires a `.cdsproj` into the project's solution file. Replace clone's rename-based workaround with a direct write, and have `clone` create `.slnx` for new projects while Flowline continues to read `.sln`.

### Problem Frame

`dotnet sln add` refuses a `.cdsproj`: *"Project has an unknown project type and cannot be added to the solution file."* It also exits 0, so a script cannot detect the failure. This is [dotnet/sdk#47638](https://github.com/dotnet/sdk/issues/47638) — the SDK cannot resolve a `DefaultProjectTypeGuid` for extensions it does not recognize.

`clone` works around this by renaming `Package.cdsproj` to `Package.csproj`, calling `dotnet sln add`, renaming it back, then string-replacing the filename inside the `.sln` (`src/Flowline/Commands/CloneCommand.cs:413-446`). Two costs follow. The workaround is reachable only from `clone`, so anyone whose project did not come from `clone` — a repo migrating off spkl, Daxif, or PACX, a second solution added later, a hand-assembled project — has no supported path. And the rename opens a crash window: an interrupted `clone` leaves `Package.csproj` on disk, at which point clone's own guard (`CloneCommand.cs:341`) tells the user to delete `Package/` and re-clone — destructive advice for a state one rename would fix.

Separately, `.slnx` has become the default for `dotnet new sln` in .NET 10, and `clone` passes `--format sln` (`CloneCommand.cs:402`) to opt out. That opt-out rests on an assumption that `.slnx` cannot hold a `.cdsproj`, which testing disproved.

### Key Decisions

- **KD1 — `flowline sln add`, a subcommand under an `sln` branch.** The user arrives here having just watched `dotnet sln add` fail, so `dotnet` → `flowline` is a one-word substitution. "Solution" already means a Dataverse solution throughout Flowline; naming the file extension avoids that collision, which `flowline add project` would invite. The branch is the first in an otherwise flat command surface (`src/Flowline/Program.cs:149-193`), accepted because a Spectre branch costs registration lines and no runtime or config surface.

- **KD2 — `.cdsproj` only; a `.csproj` argument is refused.** Flowline handles what the SDK cannot and stays out of the way otherwise. Wrapping a working first-party command would add a surface with no capability behind it.

- **KD3 — Write the solution entry directly; never rename the project file.** The rename exists only to satisfy `dotnet sln add`, and writing the entry removes both the shell-out and the crash window. Both formats are writable: `.sln` needs a project GUID and matching `ProjectConfigurationPlatforms` entries, `.slnx` needs neither.

- **KD4 — `clone` creates `.slnx` by default and falls back to `.sln` below the SDK floor; Flowline reads either format.** Verified working with a real `Package.cdsproj` on SDK 10.0.302: `dotnet sln list` enumerates it and `dotnet build` completes the SolutionPackager run and produces the zip. The fallback is not optional polish — `.slnx` requires SDK 9.0.200, and the artifact outlives the machine that created it (see KD7).

- **KD5 — Existing `.sln` projects are left alone, with no conversion and no nudge.** `.sln` is not deprecated, so there is no forcing function, and shipping a wrapper around a first-party command earns nothing. Note for anyone who does convert: `dotnet sln migrate` writes the `.slnx` but leaves the original `.sln` in place, which is the coexistence state R5a governs.

- **KD7 — The SDK floor is a downstream-consumer risk, not a Flowline-user risk, so the escape hatch is a flag rather than a version check.** Flowline ships as a `net10.0` global tool (`src/Directory.Build.props`), so anyone who can run `clone` already has tooling far above the 9.0.200 floor — a clone-time SDK check would almost never fire. The exposure is on machines that never run Flowline: a teammate, a client's build agent, a CI runner pinned to an older SDK, opening a repo the consultant committed. Clone cannot detect those, so the correct control is an explicit opt-out the consultant chooses when they know their client's toolchain, not a probe of their own machine.

- **KD6 — The `.cdsproj` entry declares `Type="C#"`.** This is what `dotnet sln migrate` emits for a real Flowline solution, so Flowline's output matches the first-party tool rather than inventing a variant. The current `.sln` path already assigns the C# project type GUID, so the semantics are unchanged.

### Requirements

**Command**

- R1. `flowline sln add <path>` adds the given `.cdsproj` to the project's solution file.
- R2. A `.csproj` argument is refused with a message directing the user to `dotnet sln add`.
- R3. The command locates the project's solution file itself and operates on whichever format is present.
- R4. Adding a project already present in the solution file succeeds without duplicating the entry.
- R5. A missing project file produces an actionable error naming the path that was not found.
- R5a. When a `.sln` and a `.slnx` sharing a base name are both present, Flowline operates on the `.slnx` and reports that the leftover `.sln` should be deleted. This is not an error state — it is what `dotnet sln migrate` leaves behind.
- R5b. When no solution file exists, `flowline sln add` creates one in the format `clone` would produce and then writes the entry. The migrating and hand-assembled projects the command exists for start from exactly this state.

**Solution file format**

- R6. `flowline sln add` and the build-target resolution in R12 read both `.sln` and `.slnx`. Format-agnostic resolution is scoped to those call sites; the multi-plugin discovery work widens it when that plan is written.
- R7. `clone` creates a `.slnx` for new projects, and accepts an explicit opt-out that produces a `.sln` instead, for teams whose build agents or teammates run below SDK 9.0.200.
- R8. A written `.cdsproj` entry declares `Type="C#"`.
- R9. A project with an existing `.sln` keeps working unchanged — Flowline never converts it, prompts about it, or warns about it.
- R9a. The `.sln`-membership language in `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md` (KD1, KD5, R1, R8, AE2, AE3) is restated as "solution file (`.sln` or `.slnx`)". That plan has no blockers and can otherwise ship first, leaving every project R7 creates invisible to plugin discovery.

**Clone and build path**

- R10. `clone` writes the `.cdsproj` solution entry directly and never renames the project file.
- R11. An interrupted `clone` never leaves a `.cdsproj` renamed to `.csproj`.
- R12. When building at the project root, Flowline names the solution file explicitly rather than relying on the working directory holding exactly one. Project-level builds (`Plugins/`, `WebResources/`) hold no solution file and are unaffected.

### Acceptance Examples

- AE1. Refusing a csproj
  - **Given:** a project with `Plugins/Plugins.csproj`.
  - **When:** the user runs `flowline sln add Plugins/Plugins.csproj`.
  - **Then:** nothing is written, and the message names `dotnet sln add` as the tool for this case.
  - **Covers R2.**

- AE2. Adding to a project that still uses `.sln`
  - **Given:** a project whose solution file is `MySolution.sln`.
  - **When:** the user runs `flowline sln add Package/Package.cdsproj`.
  - **Then:** the entry is written into the existing `.sln`, and the project is not converted to `.slnx`.
  - **Covers R3, R6, R9.**

- AE3. Re-adding a project already wired in
  - **Given:** a solution file that already references `Package/Package.cdsproj`.
  - **When:** the user runs `flowline sln add Package/Package.cdsproj`.
  - **Then:** the command reports the project is already present and the file gains no second entry.
  - **Covers R4.**

- AE4. Interrupted clone
  - **Given:** a `clone` that is terminated while setting up the solution file.
  - **When:** the user inspects `Package/`.
  - **Then:** `Package.cdsproj` is present under its own name, and re-running `clone` proceeds without asking the user to delete `Package/`.
  - **Covers R10, R11.**

- AE5. Both solution formats present after a manual migrate
  - **Given:** a project root holding both `MySolution.sln` and `MySolution.slnx`, the state `dotnet sln migrate` leaves behind.
  - **When:** the user runs `flowline sln add Package/Package.cdsproj`.
  - **Then:** the entry is written into the `.slnx`, and the output says the leftover `.sln` should be deleted. The command does not refuse.
  - **Covers R5a.**

- AE6. Building with both formats present
  - **Given:** the same dual-file root as AE5.
  - **When:** any Flowline command that builds at the project root runs.
  - **Then:** the build targets one named solution file and does not fail with MSB1011.
  - **Covers R12.**

- AE7. Adding to a project with no solution file
  - **Given:** a repo migrated off spkl with `Package/Package.cdsproj` and no `.sln` or `.slnx`.
  - **When:** the user runs `flowline sln add Package/Package.cdsproj`.
  - **Then:** a solution file is created in the format R7 would choose, and the entry is written into it.
  - **Covers R5b.**

- AE8. Cloning for a team on an older toolchain
  - **Given:** a consultant whose client's build agents run SDK 8.
  - **When:** the user runs `clone` with the `.sln` opt-out.
  - **Then:** a `.sln` is created rather than a `.slnx`, and every other clone behavior is unchanged.
  - **Covers R7.**

### Scope Boundaries

- No conversion command and no migration prompt. `dotnet sln migrate` covers this, and users who never run it are not disadvantaged.
- No other `sln` subcommands. `sln list` and `sln remove` have no demand behind them.
- Visual Studio's handling of `.cdsproj` is out of scope — see the verified behavior in Dependencies. This work neither causes nor fixes it.
- Implementing `.sln`-membership discovery for multi-plugin push stays with `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md`. R9a amends that plan's format language; it does not build the discovery.

### Should Flowline keep writing `.sln` at all?

Worth separating, because the two halves have very different costs.

**Reading `.sln` is not optional.** R6 and R9 commit to leaving existing projects alone, and every project created before this change is `.sln`. Dropping read support would break them.

**Writing `.sln` is the only discretionary part**, and its cost depends entirely on the KD3 mechanism. Hand-rolled, `.sln` is most of the work and `.slnx` is nearly free: a `.slnx` entry is one XML element, while a `.sln` entry needs a project GUID, a `Project(...)`/`EndProject` pair, a `SolutionConfigurationPlatforms` section, and per-project `ProjectConfigurationPlatforms` rows for every configuration and platform. That asymmetry is what makes "drop `.sln` writing" look attractive.

It stops looking attractive if planning adopts `Microsoft.VisualStudio.SolutionPersistence` (see Outstanding Questions). That library reads and writes both formats and is what `dotnet sln` itself uses, so the marginal cost of the second format collapses to choosing a serializer. The build-vs-buy decision therefore dominates the keep-vs-drop one, and should be made first.

**Recommendation: keep writing `.sln`, contingent on the library.** With it, the second format is close to free and R7's opt-out costs almost nothing to honor. If planning rejects the library and hand-rolls, revisit — at that point `.sln` writing is a real expense against a narrow audience, and the honest alternative is to drop the R7 opt-out and document SDK 9.0.200 as a hard floor for new projects rather than maintain a writer that almost nobody exercises.

### Dependencies and Assumptions

- `.slnx` requires SDK 9.0.200 or later to build and to use `dotnet sln`. Verified working on 10.0.302.
- Flowline ships as a `net10.0` global tool, so every Flowline user's own SDK is comfortably above that floor. The floor matters only for machines that open the repo without running Flowline.
- `.sln` and `.slnx` cannot coexist as the build target in a project root: bare `dotnet build` fails with MSB1011. R5a and R12 exist because of this.
- `dotnet sln add` refuses a `.cdsproj` on **both** formats — verified against `.sln` and `.slnx` on SDK 10.0.302, failing with "unknown project type" and, notably, **exit code 0**. Any implementation that shells out to it cannot detect the failure from the exit code. This is [dotnet/sdk#47638](https://github.com/dotnet/sdk/issues/47638).
- `dotnet sln add` accepts a `.csproj` into a `.slnx` normally — verified — so clone's existing `Plugins`/`WebResources` calls need no change.
- Visual Studio opens a `.slnx` and loads the `.csproj` projects inside it; only the `.cdsproj` fails to load, and it fails identically from a `.sln`. Verified by hand. Rider handles `.slnx` including the `.cdsproj`. So the format switch is parity for VS users, not a regression.
- Assumed, not verified: no `pac` command reads the project's solution file. `pac solution clone` creates a `.cdsproj`, and Flowline creates the solution file itself, so no dependency is expected. Cheap to falsify and expensive to reverse once projects ship in the new format — worth one `pac` invocation before R7 lands.

### Outstanding Questions

**Resolve before planning**

- Should R7 and the `sln` branch ship in v1.0, or wait for the following minor? `STRATEGY.md:109` dates v1.0 at 2026-07-20 with orphan-cleanup real-org testing still open, and KD5 rules out conversion — so whichever format v1.0 ships permanently splits the installed base. The rename removal (KD3, R10, R11) carries real user pain and none of that risk, and could land alone. This is a release call, not a requirements gap.

**Deferred to planning**

- Evaluate `Microsoft.VisualStudio.SolutionPersistence` as the read/write mechanism for both formats before hand-rolling one. It is the library behind `dotnet sln`, and this document already cites its schema for KD6's `Type` attribute. The decision governs the cost analysis above, so take it first.
- If the library is rejected, how the `.sln` writer produces the project GUID and `ProjectConfigurationPlatforms` entries.
- Whether the solution-file resolution for R3, R6, and R12 is shared with, or written ahead of, the multi-plugin discovery work.

### Sources and Research

- `src/Flowline/Commands/CloneCommand.cs:387-448` — solution file creation and the rename workaround.
- `src/Flowline/Commands/CloneCommand.cs:341` — the delete-and-re-clone guard that an interrupted rename triggers.
- `src/Flowline/Utils/DotNetUtils.cs:15-33` — builds by working directory with no explicit solution file.
- `src/Flowline/Program.cs:149-193` — the eight flat commands the new branch joins.
- `src/Flowline/Commands/DeployCommand.cs:358` — records that `.sln`-membership discovery is not yet implemented.
- `src/Directory.Build.props` — `net10.0` and `PackAsTool`, which is why the SDK floor is a downstream-consumer concern rather than a Flowline-user one (KD7).
- `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md` — the sibling plan whose `.sln` language R9a amends.
- [microsoft/vs-solutionpersistence](https://github.com/microsoft/vs-solutionpersistence) — the library behind `dotnet sln`, reading and writing both formats; the build-vs-buy candidate for KD3.
- [dotnet/sdk#47638](https://github.com/dotnet/sdk/issues/47638) — `dotnet sln add` fails for unknown project extensions.
- [Slnx.xsd](https://github.com/microsoft/vs-solutionpersistence/blob/main/src/Microsoft.VisualStudio.SolutionPersistence/Serializer/Xml/Slnx.xsd) — the `Type` attribute is schema-supported; it is absent from Microsoft Learn.
- [dotnet new sln defaults to SLNX](https://learn.microsoft.com/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default) — the .NET 10 default that `CloneCommand.cs:402` opts out of.
