---
date: 2026-06-18
topic: flowline-generator-strategy
---

# Flowline Generator Strategy

## Summary

`flowline generate` is a metadata-driven code generation hub: any artifact derived from
Dataverse metadata belongs under this command. Three generators ship: `pac` (default, always),
`xrmcontext3` (legacy F# bridge, already coded), and `xrmcontext` (rewrite, implement now
against beta). Supporting multiple generators is worth the effort — specifically for the
xrmcontext rewrite, whose integration cost is low and whose output quality gap over PAC is
real and daily. EBG V2 is dismissed. LCG-UDG is a valid future generator for late-binding
projects, not yet prioritized.

---

## Problem Frame

`flowline generate` wraps `pac modelbuilder build`. The question this brainstorm addresses:
should it stay that way?

PAC is Microsoft-backed, universal, and the tool most developers reach for first. But its
output violates .NET conventions: lowercase filenames, lowercase class names, raw
`OptionSetValue` wrappers instead of typed enums, no `ServiceContext`, no `[MaxLength]`
attributes. The code works — it just reads like a CRM 2011-era tool. Developers used to
xrmcontext-style output feel this every time they write plugin code against it.

The alternatives add implementation surface: auth complexity, integration maintenance, tools
that may not ship or stay maintained. The wrong call costs weeks of coding on the wrong thing.

**Is supporting multiple generators worth the effort?**

For the xrmcontext rewrite: yes. It is a proper `dotnet tool` (`XrmContext` v4 on NuGet)
with a small integration surface — validate availability, generate a temp `appsettings.json`,
invoke via CliWrap. Not materially harder than the PAC path. The quality gap is real: typed
enums, a `ServiceContext` for LINQ queries, proper PascalCase throughout, `[MaxLength]` and
`[DebuggerDisplay]` attributes. Switching to PAC-only means accepting this downgrade in code
quality every day.

For EBG V2: no. See [EBG V2 Analysis](#ebg-v2-analysis) below.

For xrmcontext3: already coded; cost is sunk. Keep it as a bridge, invest nothing more.

**Should you give up xrmcontext and switch to PAC-generated code?**

That is a valid simplification if the maintenance overhead feels too high. The trade-off is
concrete: PAC code is lowercase, `OptionSetValue`-heavy, and LINQ-unfriendly. Xrmcontext
output is idiomatic modern C#. The rewrite's integration is clean enough that the maintenance
cost does not justify the downgrade. The recommended answer is: keep the rewrite, skip
everything else.

---

## Key Decisions

**`flowline generate` is not limited to early-binding.** The command is a code generation
hub for any artifact derived from Dataverse metadata: early-bound C# proxies, late-binding
constants, TypeScript types, whatever comes next. The `--generator` flag selects output type
and tooling. This scoping decision matters because it prevents the command from being
refactored into a PAC-wrapper that would need to be undone later.

**PAC is the default generator, permanently.** It is Microsoft-backed, ships with every PAC
CLI install, and is what most developers expect. It stays default regardless of how many
generators are added. Developers who want better output opt in explicitly.

**`xrmcontext` is the xrmcontext rewrite (`XrmContext` v4+), not the old exe.** The flag
value is unqualified `xrmcontext` because v4 is the going-forward option. The old F# exe
(`Delegate.XrmContext` v3) carries a version qualifier: `xrmcontext3`. When v5 eventually
ships, `xrmcontext` still means "current recommended xrmcontext" with no flag rename needed.

**Implement xrmcontext rewrite against the beta now.** `XrmContext` v4.0.0-beta.25 shipped
March 13 2026. Flowline is pre-v1.0. Depending on a beta is acceptable — the rewrite will
likely hit stable before Flowline v1.0 ships.

**xrmcontext3 gets no new investment.** It is Windows-only, .NET Framework 4.6.2, and
unmaintained since September 2022. It stays in the codebase as a bridge. Bug reports in
xrmcontext3 are closed as "migrate to `--generator xrmcontext`."

**No Flowline-native generator.** The xrmcontext rewrite covers the full generation surface
including Custom APIs. Building a native generator is unnecessary.

---

## Requirements

**Generator selection**

- R1. `flowline generate` accepts `--generator {pac|xrmcontext3|xrmcontext|lcg-udg}`. Default
  is `pac` when flag and config are absent.
- R2. Generator choice persists to `.flowline` under `generate.generator` on every run.
- R3. `--generator` overrides the saved config value and updates `.flowline`.

**Generator capabilities**

| Flag | Tool | Output type | Platform | Status |
|---|---|---|---|---|
| `pac` | `pac modelbuilder build` | Early-bound C# (Microsoft-style) | Cross-platform | Default |
| `xrmcontext3` | `Delegate.XrmContext` v3 | Early-bound C# (idiomatic, F#-era) | Windows-only | Bridge, no new investment |
| `xrmcontext` | `XrmContext` v4+ dotnet tool | Early-bound C# (modern, typed enums, service context) | Cross-platform | Implement now |
| `lcg-udg` | LCG/UDG | Late-binding C# constants | TBD | Planned, not prioritized |

- R4. `xrmcontext3` is the existing implementation renamed from `xrmcontext` in code and config.
  Any `.flowline` file with `generator: xrmcontext` written by the old implementation reads as
  `xrmcontext3` after the rename. Migration strategy is a planning decision.
- R5. `xrmcontext` invokes `XrmContext` v4+ as a dotnet tool. Flowline validates availability
  and throws a `FlowlineException` with `dotnet tool install -g XrmContext` instructions if
  not found.
- R6. `xrmcontext` generates a temp `appsettings.json` from `.flowline` config, sets it as the
  working directory for the tool invocation, and discards it after the run.
- R7. `lcg-udg` is a valid generator value that throws `FlowlineException(NotImplemented)` until
  implemented, rather than being parsed as unknown input.

---

## EBG V2 Analysis

Early Bound Generator V2 (`DLaB.Xrm.EarlyBoundGeneratorV2`, by Daryl LaBar) is the #2 most-used
generator after PAC. It is actively maintained, listed in Microsoft's own docs as a recommended
quality layer, and addresses the exact casing problem PAC has. It deserves an honest assessment
rather than a footnote dismissal.

**What EBG V2 adds over raw PAC:**
EBG V2 generates a `builderSettings.json` and then calls `pac modelbuilder build` under the hood.
Its contribution is casing control: class names, property names, and enum names come out in
PascalCase instead of PAC's lowercase. That is the extent of the improvement — the underlying
output is still PAC-style: `OptionSetValue` wrappers instead of typed enums, no `ServiceContext`,
no `[MaxLength]` or `[DebuggerDisplay]` attributes.

**Why Flowline dismisses it:**

1. ~~**Compatibility bug with dotnet-tool PAC.**~~ **RESOLVED 2026-08-16.** The original objection:
   EBG V2 injects a `pac modelbuilder` extension (`DLaB.ModelBuilderExtensions`) targeting .NET
   Framework 4.8, which fails to load when PAC runs as a .NET 8 dotnet tool
   ([#535](https://github.com/daryllabar/DLaB.Xrm.XrmToolBoxTools/issues/535), now closed). That
   applies only to the *PAC CLI route*, where EBG writes `builderSettings.json`, runs
   `pac modelbuilder build --settingsTemplateFile`, and needs `DLaB.ModelBuilderExtensions.dll`
   plus `DLaB.Dictionary.txt` hand-copied into PAC's install directory so ModelBuilder's provider
   loader resolves DLaB's `ICodeWriterFilterService` / `ICustomizeCodeDomService` by type name.
   EBG's other route bypasses PAC entirely: it calls `ProcessModelInvoker` from
   `Microsoft.PowerPlatform.Dataverse.ModelBuilderLib` in-process with an `IOrganizationService`.
   That route has no PAC dependency and no DLL copying.

2. **Casing only — not typed enums.** *(Still stands.)* The output is still PAC underneath. EBG V2
   solves one of the three reasons to prefer xrmcontext; the xrmcontext rewrite solves all of them.

3. **50+ configuration options.** *(Still stands.)* EBG V1 was criticised for config clutter; EBG V2
   is more complex, not less. Flowline's value is opinionated defaults — wrapping EBG V2 would mean
   either exposing that complexity or hiding it with arbitrary choices.

4. ~~**Not a dotnet tool.**~~ **RESOLVED 2026-08-16.**
   [`DLaB.Xrm.EarlyBoundGeneratorV2.Api` 2.2026.7.28](https://www.nuget.org/packages/DLaB.Xrm.EarlyBoundGeneratorV2.Api)
   (published 30 July 2026) ships a **net10.0** TFM alongside net48, described as "Without
   XrmToolBox Dependencies". Verified against the NuGet registration API. Flowline targets net10.0,
   so a plain `PackageReference` is viable. Its only dependency, `ModelBuilderLib` 2.0.16, pulls
   `System.CodeDom`, `System.Configuration.ConfigurationManager`, `System.Text.Json` and
   `System.Xml.XmlDocument`, and does not reference `Microsoft.PowerPlatform.Dataverse.Client`, so
   Flowline's pinned 1.2.26 is unaffected.

5. **Would be Flowline's first in-process generator.** *(New, 2026-08-16, and now the strongest
   objection.)* `pac`, `xrmcontext`, and `xrmcontext3` all shell out; `IGenerator` is a
   run-a-process-and-collect-files abstraction. Referencing the EBG Api pulls ModelBuilderLib into
   Flowline's own tool package and trades process isolation for assembly-load failure modes. That is
   a first-of-kind architectural change, not one more row in the generator table.

**Not verified:** whether the net10.0 Api entry point applies DLaB's casing and filtering purely
in-proc with no file-copy step. The EBG wiki does not document how the customizations are injected,
and #535's failure was in that same provider-resolution path.

**Conclusion (unchanged):** EBG V2 is the right tool for developers who want better casing and are
already using the XrmToolBox GUI. It is not the right dependency for Flowline. The xrmcontext
rewrite is a strictly better alternative with a cleaner integration path. The verdict no longer
rests on blockers 1 and 4; it rests on EBG delivering a strict subset of what `xrmcontext` already
ships, for a first in-process dependency.

**Revisit trigger:** `XrmContext` v4 is beta, ~5.6K downloads, single small vendor. EBG rests on a
Microsoft-maintained core with far wider adoption. If v4 has not reached stable by Flowline v1.0, or
the project goes quiet, reopen this: EBG-in-process becomes the sensible hedge.

---

## Scope Boundaries

**In scope**
- `pac`, `xrmcontext3`, `xrmcontext` generators
- `xrmcontext3` rename in code and config (from current `xrmcontext` value)
- xrmcontext rewrite requirements doc: `docs/brainstorms/2026-06-18-generate-xrmcontext-rewrite-requirements.md`

**Planned, not yet prioritized**
- `lcg-udg` — late-binding C# constants; existing requirements doc at
  `docs/brainstorms/2026-06-17-generate-lcg-udg-support-requirements.md`
- TypeScript type generation — no concrete requirements yet; door is open architecturally

**Off the table**
- EBG V2 — casing-only improvement, complex config, and would be Flowline's first in-process
  generator. (The dotnet-tool PAC compatibility objection is resolved as of 2026-08-16; see
  [EBG V2 Analysis](#ebg-v2-analysis) for the revisit trigger.)
- Flowline-native generator — xrmcontext rewrite covers the full surface

---

## Dependencies / Assumptions

- `XrmContext` v4.0.0-beta.25 is usable now. Stable release expected before Flowline v1.0 but
  not guaranteed. Flowline tracks the `XrmContext` NuGet feed for stable release.
- `DefaultAzureCredential` in the xrmcontext rewrite handles most auth cases without `az login`.
  Service principal PAC profiles are handled by injecting env vars. User-profile behavior against
  real environments needs validation during implementation (see
  `docs/brainstorms/2026-06-18-generate-xrmcontext-rewrite-requirements.md` open questions).

---

## Sources / Research

- Code comparison: `C:/Users/RemyvanDuijkeren/Code/TryOut/MyFlowTest/solutions/Cr07982/Plugins/Models-pac`
  vs `Models-xrmcontext` — PAC: lowercase filenames, `OptionSetValue`, 3905-line mixed files;
  xrmcontext: PascalCase, typed enums, `ServiceContext`, `[MaxLength]`, `[DebuggerDisplay]`
- NuGet: `XrmContext` v4.0.0-beta.25 (March 2026, 5.6K downloads); `Delegate.XrmContext` v3.0.1
  (September 2022, 486K downloads)
- EBG V2 compatibility bug: github.com/daryllabar/DLaB.Xrm.XrmToolBoxTools/issues/535 (closed)
- EBG V2 output: casing only; PAC-style underneath
- EBG V2 invocation modes (2026-08-16): wiki page
  `EBG ‐ Version 2.2023.4.3 Upgrade To PAC ModelBuilder` documents both the in-process
  `ProcessModelInvoker` route and the PAC CLI route with hand-deployed extension DLLs
- NuGet registration API (2026-08-16): `DLaB.Xrm.EarlyBoundGeneratorV2.Api` 2.2026.7.28 targets
  net10.0 + net48; `Microsoft.PowerPlatform.Dataverse.ModelBuilderLib` 2.0.16 targets net6.0 + net48
  with no Dataverse.Client dependency
- Community direction: consolidating on PAC as foundation; EBG V2 as quality layer in GUI
  workflows; xrmcontext rewrite has no community awareness yet
- Related docs: `docs/brainstorms/2026-06-17-generate-xrmcontext-support-requirements.md`,
  `docs/brainstorms/2026-06-18-generate-xrmcontext-rewrite-requirements.md`,
  `docs/plans/2026-06-17-001-feat-generate-xrmcontext-generator-plan.md`
