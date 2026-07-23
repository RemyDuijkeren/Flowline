# False-positive orphan detection for a classic plugin assembly whose name contains a period

- **Status**: fixed — 2026-07-23.
- **Severity**: high — this is exactly the "wrong-answer cost: deletes a live registration" scenario
  the project-detection-rules plan explicitly worried about for plugin resolution. A user trusting
  the warning and running `--force delete-orphans` would delete a live, correctly-registered
  assembly.
- **Found**: 2026-07-21, while testing a two-plugin-project solution (one nupkg-based, one classic).

## Repro

1. Register a plugin project as a **classic (non-package)** assembly whose `.NET AssemblyName`
   contains a period, e.g. `Cr07982.LegacyPlugins` (real push, real registration — confirmed correctly
   registered and reflected by Flowline's own subsequent lookups).
2. Run `flowline sync` (or `push`/`deploy`, which run the same drift check) to unpack the solution
   from Dataverse.
3. The drift/orphan warning reports the assembly as **not having local source**, even though the
   project genuinely exists and is correctly wired into the solution file:
   ```
   Warning: 'Cr07982LegacyPlugins.dll' in Dataverse — no local plugin source, won't manage
   ```
   Note the missing dot — `Cr07982LegacyPlugins`, not `Cr07982.LegacyPlugins`.

## Root cause, confirmed

- PAC's solution-unpack strips periods from the **folder/file name** it generates for a classic
  `PluginAssembly` component: `Cr07982.LegacyPlugins` unpacks to a folder/file literally named
  `PluginAssemblies/Cr07982LegacyPlugins-<guid>/Cr07982LegacyPlugins.dll` — no dot.
- The companion `.dll.data.xml` metadata file correctly preserves the true name:
  `FullName="Cr07982.LegacyPlugins, Version=1.0.0.0, ..."` — so Dataverse's actual registration is
  fine; this is purely a PAC unpack-naming quirk.
- Nupkg-based `PluginPackage` export does **not** have this problem — `av_Cr07982.Backend` (a
  nupkg-registered plugin with a dot in its name) keeps its dot correctly in the unpacked
  `pluginpackages/` folder name.
- `PluginWebResourceDriftChecker.CheckOrphanAssemblies`/`CheckPlugins`
  (`src/Flowline/Utils/PluginWebResourceDriftChecker.cs:91-136`) compare **local build-output
  filenames** against the **unpacked folder's filenames** by raw case-insensitive string match — so
  any classic plugin assembly whose `AssemblyName` contains a period will permanently misreport as an
  orphan, even while actively in use and correctly registered.

## Fix applied

`PluginWebResourceDriftChecker` now resolves each unpacked `PluginAssemblies/` DLL to its true name
before comparing, via a new `ResolveUnpackedAssemblyName(dllPath)` helper: it reads the companion
`<dll>.data.xml`'s `FullName` (simple name before the first comma) and appends `.dll`, falling back
to the on-disk filename when the metadata is absent or unreadable. Both call sites use it —
`CheckPlugins`'s `srcDlls` dictionary key and `CheckOrphanAssemblies`'s per-file name — so a dotted
classic assembly (`Cr07982.LegacyPlugins`, unpacked dot-stripped as `Cr07982LegacyPlugins.dll`) now
matches build output's `Cr07982.LegacyPlugins.dll` and is no longer misreported as an orphan. Warnings
that do fire now name the assembly by its true dotted identity. The parser is tolerant of `FullName`
as either an attribute or an element, and swallows `XmlException` (falls back to filename) so a
malformed `.data.xml` never throws.

Nupkg `PluginPackage` exports were never affected (they keep the dot in the on-disk name) and any DLL
with no readable metadata falls through to the old filename compare — both keep working unchanged.

Regression tests: `tests/Flowline.Tests/PluginWebResourceDriftCheckerTests.cs` —
`Check_ClassicDottedAssembly_PacStrippedFilename_NotReportedAsOrphan` (the exact live bug shape),
`Check_ClassicDottedAssembly_FullNameAsElement_NotReportedAsOrphan` (element-form tolerance),
`Check_ClassicDottedAssembly_SizeMismatchReportedUnderTrueName`,
`Check_ClassicDottedAssembly_NoBuildingProject_ReportedAsOrphanUnderTrueName` (a genuine orphan is
still flagged, under its true name), and `Check_UnpackedAssembly_MalformedDataXml_FallsBackToFileName`.
`Flowline.Tests` green (929 passing; the 2 failing `FlowlineCommandTests.ConnectToDataverseAsync_*`
tests are pre-existing live-MSAL-auth failures unrelated to this change, confirmed failing identically
with the fix stashed).

## Live-verified against real PAC output (2026-07-23)

Verified against **real** PAC-unpacked output, not just synthesized XML. Flowline's own MSAL session
was expired this run (interactive re-auth unavailable), so the live check was routed through PAC's
still-valid connection instead: `pac solution export --name Cr07982` (from DEV) + `pac solution
unpack`. The real output confirms the exact bug shape and the parser's assumption:

- The classic assembly `Cr07982.LegacyPlugins` unpacks to a **dot-stripped** on-disk file
  `Cr07982LegacyPlugins.dll`, inside a `Cr07982LegacyPlugins-<GUID>/` folder.
- Its companion `Cr07982LegacyPlugins.dll.data.xml` carries the true identity as a **`FullName`
  attribute on the root `<PluginAssembly>` element** (the primary form the parser reads):
  `FullName="Cr07982.LegacyPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=48c2f23af73ee643"`.
  Simple name before the first comma = `Cr07982.LegacyPlugins` → matches build output
  `Cr07982.LegacyPlugins.dll`. The file also carries a **UTF-8 BOM**, which `XDocument.Load` tolerates.

A throwaway probe test drove the **deployed** `PluginWebResourceDriftChecker.CheckAsync` against the
real exported folder (real dll + real BOM-prefixed `.data.xml`) with a byte-identical build output
under the true dotted name: result was **empty (no orphan)**, confirming the fix on real data. The
probe is discriminating — without the fix, the dot-stripped dictionary key would not match the dotted
release dll and it would flag as an orphan. Probe removed after running; the committed regression
tests in `PluginWebResourceDriftCheckerTests.cs` cover the same shapes.

**Not yet run**: the full end-to-end `flowline sync`/`push`/`drift` command against this assembly
(those need Flowline's own Dataverse connection, blocked on expired MSAL auth this run). The
drift-detection logic they invoke is the exact code path the probe exercised directly.
