# False-positive orphan detection for a classic plugin assembly whose name contains a period

- **Status**: not fixed — needs the drift checker to read the true assembly name from metadata XML
  instead of trusting the on-disk filename; more than a one-line fix.
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

## Suggested fix direction (not attempted)

When matching a classic plugin assembly for drift/orphan purposes, read the true name from the
companion `.dll.data.xml`'s `FullName` attribute (the simple name before the first comma) rather than
trusting the on-disk filename PAC generated. This affects both `CheckPlugins` (size-mismatch
comparison) and `CheckOrphanAssemblies` (orphan flagging) in
`src/Flowline/Utils/PluginWebResourceDriftChecker.cs`.
