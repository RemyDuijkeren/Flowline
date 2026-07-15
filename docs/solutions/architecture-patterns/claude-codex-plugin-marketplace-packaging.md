---
title: Claude Code / Codex plugin marketplace - catalog file lives at repo root, not inside the plugin folder
date: 2026-07-15
category: docs/solutions/architecture-patterns/
module: flowline-agent-plugin
problem_type: architecture_pattern
component: tooling
severity: medium
tags: [claude-code, codex, plugin, marketplace, skill-discovery, agent-plugin, packaging]
---

# Claude Code / Codex plugin marketplace - catalog file lives at repo root, not inside the plugin folder

## Context

While building the Claude Code / Codex agent plugin for Flowline, an earlier private strategy doc (`docs/others/AI-Strategy.md`, gitignored) sketched the plugin's packaging layout from inference/memory rather than verified documentation. Two structural assumptions in that sketch were wrong:

1. It assumed the marketplace catalog file lived *inside* the plugin's own folder, next to `plugin.json`.
2. It assumed `plugin.json` needed an explicit `"skills"` manifest field (`"skills": "./skills/*/SKILL.md"`) to be discovered.

Neither mistake was caught by writing or reading the sketch itself — both looked plausible on the page. They were only caught when it came time to actually implement the plugin and someone fetched the current official docs and then ran the real CLI commands end-to-end.

## Guidance

Before implementing an agent-plugin package for any ecosystem (Claude Code, Codex, and presumably others later), do two things in order — neither is sufficient alone:

1. **Verify packaging mechanics against the current official docs.** Don't sketch file layout or manifest schema from memory, prior projects, or a similar-sounding tool. Fetch the actual current docs for the target platform.
2. **Run real CLI round-trips on every target ecosystem**, not just a read of the docs. The verified sequence for this plugin:
   `claude plugin validate ./plugin --strict` → `claude plugin marketplace add <local-path>` → `claude plugin install <plugin>@<marketplace>` → `claude plugin details <plugin>@<marketplace>` (confirms actual installed skill list and token-cost estimate) → repeat the same four-step sequence with `codex plugin marketplace add` / `codex plugin add` → inspect the installed plugin's on-disk cache directory to directly confirm which files were actually copied and discovered as skills.

   Every step in that chain surfaces information the previous step's success does not guarantee — e.g. `--strict` validation and "required to load" are different bars (see below).

**Corrected fact #1 — marketplace file location, per ecosystem.**

- Claude Code: `.claude-plugin/marketplace.json` lives at the **repo root** ("marketplace root"), one directory level above the plugin's own `.claude-plugin/plugin.json`. A same-repo entry's `"source"` is a relative path string (e.g. `"source": "./plugin"`), resolved relative to the marketplace root — not relative to `.claude-plugin/`.
- Codex: same two-file split, different paths/shape. Catalog at `$REPO_ROOT/.agents/plugins/marketplace.json` (also repo root, not inside the plugin folder). A same-repo entry's `"source"` is commonly written as an object, `{"source": "local", "path": "./plugin"}` (what this repo uses) — Codex's docs also permit a plain string path for local entries, so the object form isn't strictly required, just the more explicit option.

**Corrected fact #2 — no `skills` field needed for the default case.** Claude Code always scans the plugin's default `skills/` directory unconditionally. The `plugin.json` `skills` field is **additive** — only for declaring *extra*, non-default skill directories — and it takes a plain directory path (string or array, e.g. `"./custom/skills/"`), not a glob pointing at `SKILL.md` files. The sketch's `"./skills/*/SKILL.md"` was both unnecessary and the wrong shape.

Also verified in the same pass: Claude Code's `plugin.json` requires only `name` (docs state this explicitly). Codex's docs don't state an explicit required-fields list the same way, but its quickstart and manual-creation examples always populate `name`, `version`, and `description` together — no example anywhere shows a name-only Codex manifest, unlike Claude Code's explicit name-only allowance. Treat "populate all three for Codex" as the safe convention, not a formally documented requirement.

Corrected repo-root layout:

```
Flowline/
├── .claude-plugin/marketplace.json    ← Claude Code catalog, repo root
├── .agents/plugins/marketplace.json   ← Codex catalog, repo root
└── plugin/
    ├── .claude-plugin/plugin.json     ← Claude Code manifest (name only required; no "skills" field)
    ├── .codex-plugin/plugin.json      ← Codex manifest (name+version+description — the safe convention, not a documented hard requirement)
    └── skills/
        ├── flowline/SKILL.md
        └── flowline-migration/SKILL.md
```

## Why This Matters

A wrong assumption about marketplace file location isn't a partial degradation — it's total, silent failure. If `.claude-plugin/marketplace.json` had shipped nested inside `plugin/` as the sketch assumed, `/plugin marketplace add` would never find the catalog. No error pointing at the real cause, no partial functionality to notice — just "the plugin isn't there," discoverable only by actually attempting an install, not by re-reading the design doc.

Sketching packaging structure from memory or inference — even confident-sounding memory, even when the ecosystems are similar-but-not-identical (Claude Code vs. Codex share the two-file-split concept but diverge on path and source shape) — is exactly the failure mode that produces a plugin that looks correct in a design doc and simply does not work. The two ecosystems being *almost* the same is itself a trap: it invites carrying one ecosystem's verified layout over to the other without re-checking.

Separately, this session's `--strict` validation run flagged a missing `version` field as a warning even though only `name` is required to *load* — a finding worth attributing to that run specifically rather than treating as settled doc text, since the shipped manifest now already includes `version` (nothing to re-reproduce against) and the current official docs frame `--strict` primarily around unrecognized/misspelled fields, not missing optional ones. If accurate, it still shows "should work per the docs" and "passes the platform's own strict tooling" can be two different bars — but verify again before leaning on it as a general rule.

## When to Apply

- Building or updating any Claude Code, Codex, or similar agent-plugin packaging (marketplace catalogs, plugin manifests, skill/command directory conventions).
- Any time a design or strategy doc makes a factual claim about an external platform's file layout, manifest schema, or required fields, and that claim has not been checked against the platform's *current* official docs (platforms change; memory of a prior version is not verification).
- Any time two target ecosystems look structurally similar (same two-file split, same general concept) — verify each independently rather than assuming the second matches the first.
- Any time "the docs say this is required/optional" and "the CLI's own validator accepts/rejects this" might diverge — run the validator, don't infer from prose alone.

## Examples

Corrected file tree (see above) — marketplace catalogs at repo root for both ecosystems, plugin manifests one level down inside `plugin/`.

Before/after on the `skills` field in Claude Code's `plugin.json`:

```json
// Before (AI-Strategy.md sketch — wrong shape, unnecessary for default location)
{
  "name": "flowline",
  "skills": "./skills/*/SKILL.md"
}
```

```json
// After (verified against docs — skills/ is scanned by default, field omitted)
{
  "name": "flowline"
}
```

## Related

- `docs/plans/2026-07-15-002-feat-flowline-agent-plugin-plan.md` — Key Technical Decisions KTD1 (marketplace file location, one per ecosystem), KTD2 (no `skills` field needed), KTD3 (Codex's stricter version requirement, decoupled from the CLI's own NuGet version). Shipped on `master` (commits `43544b4`, `1f298bc`).
- Source docs verified this session: https://code.claude.com/docs/en/plugins-reference, https://code.claude.com/docs/en/plugin-marketplaces, https://learn.chatgpt.com/docs/build-plugins
