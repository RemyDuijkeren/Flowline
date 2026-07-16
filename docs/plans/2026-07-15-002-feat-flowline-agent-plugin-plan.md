---
title: Flowline Agent Plugin - Plan
type: feat
date: 2026-07-15
topic: flowline-agent-plugin
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
deepened: 2026-07-15
---

# Flowline Agent Plugin - Plan

## Goal Capsule

**Objective:** Ship Flowline as a Claude Code / Codex plugin so an agent already knows how to drive Flowline before any project exists — reaching greenfield and brownfield (spkl/Daxif/PACX/ALM-Accelerator) repos that today's per-project `AGENTS.md` structurally can't.

**Product authority:** `docs/others/AI-Strategy.md` §5–§6 (the decision record this plan extends) and `STRATEGY.md` (persona, adoption metrics).

**Open blockers:** None. Research resolved the packaging unknowns (marketplace file location, manifest schema for both Claude Code and Codex) that were open at requirements time. `flowline-ci` and `flowline-drift` both stay out of scope — `flowline-drift`'s deferral premise was partially stale (`flowline drift` shipped, but only its narrower orphan-direction half) and was re-confirmed as deferred rather than silently carried forward; see Scope Boundaries.

## Product Contract

**Product Contract preservation:** unchanged — R1–R9 carried verbatim from the requirements-only version; this pass adds Planning Contract, Implementation Units, Verification Contract, and Definition of Done.

### Summary

Ship a `plugin/` folder inside the Flowline repo plus a repo-root marketplace catalog, with two Claude Code / Codex skills for v1: `flowline` (the core clone → push → sync → deploy loop) and `flowline-migration` (detects, guides, and verifies migration off spkl / Daxif / PACX / ALM-Accelerator). Distributed via `/plugin marketplace add RemyDuijkeren/Flowline`.

### Key Decisions

- **Plugin lives inside the Flowline repo (Option A), not a separate repo.** Skew risk — a skill documenting a CLI flag the installed version doesn't have — is structurally impossible when skills and CLI version together in one release. This matters more with two skills shipping than with the single-skill plan originally sketched. See `AI-Strategy.md` §5.
- **v1 scope is deliberately small: two skills only.** No user has asked for AI-agent integration yet — this is a strategic bet, not a response to demand. `flowline-drift` and `flowline-ci` are fast-follow / gated, not v1.
- **`flowline-migration` goes beyond "read the wiki guide to me."** It detects migration-source signatures unprompted, guides the user through the matching wiki guide, then verifies the result by running existing `flowline` commands — not just trusting that steps were followed.
- **No new CLI code for either v1 skill.** Both are pure markdown + manifest; `flowline-migration` reuses existing wiki content end to end.

### Requirements

**Packaging**
- R1. A `plugin/` folder exists at the Flowline repo root, discoverable via `/plugin marketplace add RemyDuijkeren/Flowline`, with a Claude Code manifest (`.claude-plugin/plugin.json`) and a Codex manifest (`.codex-plugin/plugin.json`). A Cursor manifest is deferred until the other two are proven in use.
- R2. Skills are auto-discovered from `plugin/skills/<name>/SKILL.md` — no explicit per-skill glob declaration beyond what the manifest already needs.

**`flowline` (core skill)**
- R3. Teaches an agent to detect a Flowline project (`.flowline` at root + `solutions/<Name>/`) and, when absent, suggest `flowline clone` or standalone `push --pluginFile`.
- R4. Documents the core loop: edit code with `[Step]`/`[Filter]`/`[CustomApi]`/`flowline:onload`/`onsave`/`onchange` → `push --dry-run` → read blast radius → `push` → `sync` → `deploy`.
- R5. States the exit-code contract: branch on exit codes, not output text; error messages embed the fix command; a zero exit from `push` doesn't mean the change is verified — behavior must still be checked.
- R6. States the authority rule: `push` treats the repo as authoritative (deletes DEV orphans); `sync` treats DEV as authoritative; never push over un-synced PROD changes.

**`flowline-migration`**
- R7. Detects migration-source signatures unprompted (`spkl.json`, Daxif config, PACX folders, ALM-Accelerator marker) the same way the core skill detects `.flowline` presence.
- R8. Guides the user conversationally through the matching wiki migration guide (spkl / Daxif / PACX / ALM-Accelerator).
- R9. Verifies the migration by running `flowline push --dry-run` and `flowline sync` against the migrated project and reporting the result — migration isn't reported as done just because the guide's steps were followed.

### Key Flows

**F1. Migration flow (`flowline-migration`).** Covers R7, R8, R9.
**Trigger:** agent opens a repo containing `spkl.json`, a Daxif config, a PACX folder, or an ALM-Accelerator marker, with the plugin installed.
1. Skill recognizes the signature and proactively offers migration (e.g. "this looks like a spkl project — want me to migrate it to Flowline?").
2. On accept, agent walks the user through the matching wiki guide's steps.
3. Agent runs `flowline push --dry-run` then the phase-appropriate sync check (`flowline sync` for a project-mode migration, `pac solution sync` directly for a standalone-only migration — KTD6) against the result and reports whether they succeeded.
4. Migration is only reported as done after step 3 confirms it, not after step 2's steps are followed.

The core skill's own loop flow is unchanged from `AI-Strategy.md` §5's existing sketch — not re-specified here since this plan made no new decision about it.

### Scope Boundaries

**Deferred for later (fast-follow / gated)**
- `flowline-drift` — deferred. `flowline drift` has shipped (`Program.cs:188`, `src/Flowline/Commands/DriftCommand.cs`), but only its narrower orphan-direction half (per `Flowline.wiki/03-Command-Reference.md#drift`'s own scope note); the full promotion-audit vocabulary the `flowline-drift` skill was designed around (§4.4 in `AI-Strategy.md`) is still not built. Decision (2026-07-15): stays deferred — revisit once `drift` covers both directions and adopts that vocabulary.
- `flowline-ci` — gated on a new CI/CD wiki guide being written first; no such content exists today (confirmed: no CI page in `Flowline.wiki`).
- Cursor manifest — deferred until Claude Code + Codex distribution is proven.

**Outside this product's identity**
- MCP server, hooks, subagents, and slash commands as plugin surfaces — all explicitly rejected in `AI-Strategy.md` §5 (Bash + CLI already gives an agent full control; each adds engineering surface for no capability gain).
- `flowline-review` (a code-review lens on attribute/annotation misuse) — considered, not selected; doesn't clear a distinct-leverage bar over the core skill's own `## Contract` section.
- Splitting the core skill into several narrower per-domain skills — rejected via the breadth mission test in `AI-Strategy.md` §9; Flowline stays one narrow skill for the loop it owns.

**Deferred to Follow-Up Work**
- Correct `AI-Strategy.md` §5's plugin layout diagram to show the repo-root `.claude-plugin/marketplace.json` (see Sources / Research below) — a small doc fix, not gating this plan.
- Correct `AI-Strategy.md` §6/§10's stale "`flowline drift` planned, not shipped" framing — same small-doc-fix shape, independent of the open product question below.

### Success Criteria

- A community mention of someone actually using the agent + Flowline workflow (e.g., "installed the plugin, had Claude walk me through migrating off spkl") — distinct from `STRATEGY.md`'s existing general adoption metrics (NuGet downloads, GitHub stars, generic community posts).

### Dependencies / Assumptions

- Assumes no direct user has requested AI-agent integration; this is a strategic bet, not validated demand. If the community-mention signal above doesn't materialize within a reasonable window after release, that's a signal to reassess before investing further in `flowline-ci` or `flowline-drift`.
- `flowline-migration`'s verify step depends on the migrated project reaching a runnable state. This differs by migration depth (KTD6): a project-mode migration needs a valid `.flowline` config and an existing unmanaged target solution before `flowline sync` will succeed; a standalone-only (Phase 1) migration has neither — `flowline sync` doesn't add much without project-mode adopted, so verify uses `pac solution sync` directly instead, which requires no `.flowline` config.

### Sources / Research

- `docs/others/AI-Strategy.md` §5–§6 — full Option A/B tradeoff analysis and the existing core-skill sketch; this plan's Key Decisions extend it, not replace it.
- `Flowline.wiki/13-Migration-from-spkl.md`, `14-Migration-from-Daxif.md`, `16-Migration-from-PACX.md`, `15-Migration-from-ALM-Accelerator.md` — the four guides `flowline-migration` operationalizes.
- `Flowline.wiki/10-AI-Agents.md` — confirms the current AGENTS.md / exit-code contract the core skill documents; confirms no CI/CD guide exists yet (gates `flowline-ci`).
- `STRATEGY.md` — primary persona (solo/small-team Dataverse technical consultant) and existing adoption metrics this plan's Success Criteria extends.
- [Plugins reference](https://code.claude.com/docs/en/plugins-reference) — verified `.claude-plugin/plugin.json` schema: `name` is the only required field; `skills` is additive to (not a replacement for) the default `skills/` scan and only needed for non-default paths; a plugin with a `SKILL.md` at its root needs no manifest at all.
- [Plugin marketplaces](https://code.claude.com/docs/en/plugin-marketplaces) — verified `.claude-plugin/marketplace.json` lives at the **repo root**, not inside `plugin/`; plugin entries use a `"source": "./plugin"` relative path for a same-repo subfolder.
- [Codex — Build plugins](https://learn.chatgpt.com/docs/build-plugins) — Codex's `.codex-plugin/plugin.json` requires `name`, `version`, and `description` (stricter than Claude Code's name-only). Codex's catalog file lives at `$REPO_ROOT/.agents/plugins/marketplace.json` and requires `name`, `interface.displayName`, and a `plugins[]` array; a same-repo subfolder plugin uses `"source": {"source": "local", "path": "./plugin"}`. Users add it via `codex plugin marketplace add owner/repo`.

---

## Planning Contract

### Key Technical Decisions

- **KTD1 — Marketplace catalog is a separate file from the plugin manifest, one per ecosystem.** `.claude-plugin/marketplace.json` and `.agents/plugins/marketplace.json` both live at the Flowline repo root (each ecosystem's marketplace root); `plugin/.claude-plugin/plugin.json` and `plugin/.codex-plugin/plugin.json` are the plugin's own manifests one level down. `AI-Strategy.md`'s original plugin-layout sketch didn't show either marketplace file — this plan corrects that (see Sources / Research, Deferred to Follow-Up Work).
- **KTD2 — No `skills` field in the Claude Code manifest.** The skills live at the default `plugin/skills/` location, which Claude Code always scans; declaring `"skills": "./skills/*/SKILL.md"` (as `AI-Strategy.md`'s original sketch showed) isn't how the field works — it takes a directory path and is only for *additional* non-default locations. Omit the field entirely.
- **KTD3 — Codex manifest version is independent of the CLI's NuGet version.** Codex requires an explicit `version` (Claude Code doesn't — it falls back to the git commit SHA when omitted). Start the Codex manifest at `0.1.0` and bump it manually only when skill content changes; don't try to keep it in lockstep with Flowline's own release version — the two are versioned by different concerns.
- **KTD4 — No shared `## Detect` text between the two skills.** Each skill's own frontmatter `description` is what Claude Code uses to decide relevance per session; the platform reads all installed skills' descriptions independently. `flowline` and `flowline-migration` don't need coordinating text to avoid conflicting — a repo either has `.flowline` (routes to `flowline`) or a migration-source signature (routes to `flowline-migration`). Resolves the requirements-only plan's open question on this point. **Tie-break for the dual-signature case:** a repo can have both `.flowline` present and a leftover migration-source file (e.g., a stale `spkl.json` from a completed migration) — `.flowline` presence wins, since it's the stronger, explicit signal of an already-configured project. `flowline-migration` stays silent rather than re-offering migration on an already-migrated repo.
- **KTD5 — `flowline-migration`'s verify step has a real side effect, not just a read.** `sync` (part of the verify step) treats DEV as authoritative and mutates the locally unpacked solution XML (R6) — it isn't inert. This is safe here because F1 gates the verify step behind the user's explicit accept in step 2; the user already approved the migration before verify runs. Re-running verify after a failed attempt is also safe: `sync` is idempotent against DEV's current state, so a second run re-captures whatever DEV holds now rather than compounding the earlier failure.
- **KTD6 — Verify branches by migration depth.** A project-mode migration (the user completed the wiki guide's Phase 2, adopting `.flowline`) verifies with `flowline push --dry-run` + `flowline sync`, as originally planned. A standalone-only migration (deliberately stopped at the wiki guide's Phase 1, per that guide's own framing — no `.flowline` yet) has no project for `flowline sync` to operate on — `SyncCommand` always requires one (`RequiresProject => true`, never overridden), so running it here would report `ConfigInvalid` (exit 11), a structural mismatch, not a migration failure, and `flowline sync` doesn't add much without project-mode adopted anyway. For this case, verify runs `pac solution sync` directly instead.
- **KTD7 — Cross-reference tripwire between wiki guides and their SKILL.md summaries.** `flowline-migration`'s `## Guide` section summarizes each wiki page's steps rather than linking out (KTD-adjacent to the Option A skew-risk argument, but one level up: `Flowline.wiki` is a separate repo with its own commit history). Add a one-line comment at the top of each of the four wiki guides and its mirrored `## Guide` subsection naming the other as a paired reference, so a future edit to either prompts a check of the other. Cheap — no automation, just a naming convention — and closes the one drift vector Option A's same-repo argument doesn't already cover.

### Output Structure

```text
Flowline/
├── .claude-plugin/
│   └── marketplace.json          ← Claude Code marketplace catalog, repo root (U1)
├── .agents/plugins/
│   └── marketplace.json          ← Codex marketplace catalog, repo root (U6)
└── plugin/
    ├── .claude-plugin/
    │   └── plugin.json           ← Claude Code manifest (U2)
    ├── .codex-plugin/
    │   └── plugin.json           ← Codex manifest (U3)
    └── skills/
        ├── flowline/
        │   └── SKILL.md          ← core loop skill (U4)
        └── flowline-migration/
            └── SKILL.md          ← migration skill (U5)
```

### Open Questions

None — the `flowline-drift` scope question is resolved; see Scope Boundaries.

---

## Implementation Units

### U1. Marketplace catalog file

**Goal:** Make Flowline discoverable via `/plugin marketplace add RemyDuijkeren/Flowline`.
**Requirements:** R1
**Dependencies:** U2, U4, U5 (references the finished plugin folder)
**Files:** `.claude-plugin/marketplace.json` (new, repo root)
**Approach:** `name: "flowline"`, `owner: {"name": "Remy Duijkeren"}`, `plugins: [{"name": "flowline", "source": "./plugin", "description": "...", "author": {"name": "Remy Duijkeren"}}]`. Omit `version` in the marketplace entry — let it fall back to git commit SHA per KTD3's reasoning (skills should track the exact commit, not a hand-bumped number).
**Patterns to follow:** Schema verified against [Plugin marketplaces](https://code.claude.com/docs/en/plugin-marketplaces) (Sources / Research above) — no existing local pattern, this is the repo's first marketplace file.
**Test scenarios:**
- Test expectation: none — static JSON config, not unit-testable. Verified via manual install (see Verification below).
**Verification:** `claude plugin marketplace add <local path to repo>` resolves the catalog and lists the `flowline` plugin without errors.

### U6. Codex marketplace catalog file

**Goal:** Make Flowline discoverable via `codex plugin marketplace add RemyDuijkeren/Flowline`.
**Requirements:** R1
**Dependencies:** U3, U4, U5 (references the finished plugin folder)
**Files:** `.agents/plugins/marketplace.json` (new, repo root)
**Approach:** `name: "flowline"`, `interface.displayName: "Flowline"`, `plugins: [{"name": "flowline", "source": {"source": "local", "path": "./plugin"}, "policy": {"installation": "AVAILABLE", "authentication": "ON_INSTALL"}, "category": "Productivity"}]`.
**Patterns to follow:** Schema verified against [Codex — Build plugins](https://learn.chatgpt.com/docs/build-plugins) (Sources / Research above) — no existing local pattern, this is the repo's first Codex marketplace file.
**Test scenarios:**
- Test expectation: none — static JSON config, not unit-testable. Verified via manual install (see Verification below).
**Verification:** `codex plugin marketplace add <local path to repo>` resolves the catalog and lists the `flowline` plugin without errors.

### U2. Claude Code plugin manifest

**Goal:** Minimal, valid `plugin.json` for the plugin folder.
**Requirements:** R1, R2
**Dependencies:** none
**Files:** `plugin/.claude-plugin/plugin.json`
**Approach:** Only `name: "flowline"` is required. Add `displayName`, `description`, `homepage` (link to the GitHub Wiki), `repository`, `license`, `keywords` for discoverability. Do **not** add a `skills` field (KTD2).
**Patterns to follow:** [Plugins reference](https://code.claude.com/docs/en/plugins-reference) complete schema example (Sources / Research above).
**Test scenarios:**
- Test expectation: none — static config.
**Verification:** `claude plugin validate ./plugin --strict` passes with zero errors or warnings.

### U3. Codex plugin manifest

**Goal:** Valid `plugin.json` for Codex's stricter schema.
**Requirements:** R1
**Dependencies:** none
**Files:** `plugin/.codex-plugin/plugin.json`
**Approach:** Include `name`, `version` (start at `0.1.0`, per KTD3), and `description` — all required by Codex, unlike Claude Code. Add `author`, `homepage`, `repository`, `license`, `keywords` to mirror U2's metadata where the schema supports it.
**Patterns to follow:** [Codex — Build plugins](https://learn.chatgpt.com/docs/build-plugins) (Sources / Research above).
**Test scenarios:**
- Test expectation: none — static config.
**Verification:** Manifest parses as valid JSON and includes all three Codex-required fields; no automated Codex-side validator is known, so this is a manual schema check against the source above.

### U4. `flowline` core skill

**Goal:** Encode the core loop, detection, contract, and exit-code table so an agent can drive Flowline end to end.
**Requirements:** R3, R4, R5, R6
**Dependencies:** none
**Files:** `plugin/skills/flowline/SKILL.md`
**Approach:** Frontmatter `name: flowline` + `description` (the only signal Claude Code sees before loading the skill — must name Dataverse ALM, plugin registration, web resources, and solution deploy so it's recognized as relevant). Body sections: `## Detect` (`.flowline` presence → suggest `clone`; absence + DLL-only task → standalone `push --pluginFile`), `## Core loop` (edit → `push --dry-run` → read blast radius → `push` → `sync` → `deploy`), `## Contract` (exit-code branching, fix-command-in-message, push-succeeded-≠-verified), `## Exit codes` table.
**Patterns to follow:** `AI-Strategy.md` §5's existing skill sketch (content basis); `Flowline.wiki/10-AI-Agents.md` exit-code table (pull verbatim rather than re-deriving); `docs/solutions/architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md` for the exit-code/AGENTS.md contract rationale.
**Test scenarios:** behavioral scenarios verified manually against a running Claude Code session — no automated test framework applies to markdown instructions, but each scenario below has a named trigger and a pass condition tied to a requirement, not a bare "none."
- `.flowline` present and valid at repo root → agent proposes `push --dry-run` as the next step, not `clone`. Covers R3.
- No `.flowline` and no `solutions/` folder → agent proposes `flowline clone`. Covers R3.
- No `.flowline`, but a migration-source signature (spkl/Daxif/PACX) is present → agent defers to `flowline-migration` instead of applying core-skill advice to an unmigrated project. Covers R3, KTD4.
- `push` exits 0 → agent states that registration succeeded, not that the change is behaviorally verified; does not declare the task done on exit code alone. Covers R5.
- `push` exits non-zero → agent branches on the exit code itself, not by scraping output text, and extracts the fix command embedded in the error message. Covers R5.
- DEV has changes not yet captured by `sync` → agent applies the authority rule and surfaces the drift instead of silently pushing over it. Covers R6.
**Verification:** Install the plugin locally in a scratch Flowline test project; open a Claude Code session; walk through each scenario above and confirm the agent's proposed action matches.

### U5. `flowline-migration` skill

**Goal:** Detect, guide, and verify migration off spkl / Daxif / PACX / ALM-Accelerator, per F1.
**Requirements:** R7, R8, R9
**Dependencies:** none
**Files:** `plugin/skills/flowline-migration/SKILL.md`
**Approach:** Frontmatter `name: flowline-migration` + `description` naming the four source tools so Claude Code recognizes relevance. Body sections: `## Detect` (exact signature files/folders per tool — confirm the precise filenames against each wiki guide while writing, e.g. `spkl.json`, `*.dxproj` or Daxif's config marker, PACX's folder convention), `## Guide` (summarize the matching wiki page's steps per tool, don't just link out; include the guide's own commit step between scaffolding and the first push so `Package/src/` isn't left uncommitted going into Verify), `## Verify` (run `flowline push --dry-run`, then `flowline sync` for a project-mode migration or `pac solution sync` directly for a standalone-only migration — KTD6; report pass/fail; never claim migration is done on guide-steps-followed alone; treat a `DirtyWorkingDirectory` exit (12) from this step as "commit the scaffolded `Package/src/` first," not migration failure — none of the wiki guides include a commit step between scaffolding and the first push).
**Patterns to follow:** `Flowline.wiki/13-Migration-from-spkl.md` through `15-Migration-from-ALM-Accelerator.md` for the exact per-tool steps; U4's `## Detect` section for consistent signature-routing style (KTD4 — no shared text needed, just a consistent shape). Add the KTD7 cross-reference comment to each wiki guide and its mirrored `## Guide` subsection while authoring this unit.
**Test scenarios:** behavioral scenarios verified manually against a running Claude Code session and synthetic fixtures — no automated test framework applies to markdown instructions, but each scenario below has a named trigger and a pass condition tied to a requirement, not a bare "none."
- `spkl.json` present, no `.flowline` → agent recognizes it unprompted and offers migration. Covers R7, F1.
- User accepts the migration offer for a detected source (e.g. spkl) → agent's guide step summarizes that source's specific wiki-guide steps, not generic migration advice. Covers R8.
- Both `.flowline` present and a stale migration-source file (e.g. leftover `spkl.json` from a completed migration) → agent does not misfire a migration offer on an already-migrated repo. Covers R7, KTD4 (false-positive guard).
- Fixture where migration is deliberately left incomplete → verify step reports failure honestly, not success. Covers R9, F1 step 4.
- `push --dry-run` passes but `sync` fails (or vice versa) → agent reports partial failure naming which step failed, and does not report migration as done. Covers R9.
- User asks "are we done?" before the verify step (F1 step 3) has run → agent declines to confirm completion. Covers R9, F1 step 4.
- User deliberately stops at the wiki guide's Phase 1 (standalone, no `.flowline` adopted) → verify uses `pac solution sync` directly instead of `flowline sync`, and does not report `ConfigInvalid` as a migration failure. Covers R9, KTD6.
- `Package/src/` freshly scaffolded and not yet committed when verify runs → agent recognizes the resulting `DirtyWorkingDirectory` exit (12) as "commit the scaffold first," not migration failure. Covers R9.
**Verification:** Point the plugin at a synthetic fixture repo containing a `spkl.json` (or Daxif/PACX equivalent); walk through each scenario above, including re-running verify after fixing a deliberately-broken fixture (KTD5 — confirms re-verify is safe and doesn't compound the earlier failure).

---

## Verification Contract

| Check | Command / method | Applies to |
|---|---|---|
| Manifest validation | `claude plugin validate ./plugin --strict` | U2 |
| Marketplace resolution | `claude plugin marketplace add <local path>` | U1 |
| Codex marketplace resolution | `codex plugin marketplace add <local path>` | U6 |
| Local install | `/plugin marketplace add <local path>` then `/plugin install flowline@flowline` in a Claude Code session | U1, U2, U4, U5 |
| Core-skill behavior | Manual walkthrough of U4's listed test scenarios in a scratch Flowline project (see U4 Verification) | U4 |
| Migration-skill behavior | Manual walkthrough of U5's listed test scenarios against a synthetic spkl/Daxif/PACX fixture, plus the re-verify-after-failure check described in U5 Verification (additional to the listed scenarios) | U5 |
| Codex manifest schema | Manual field check against the Codex docs cited in Sources / Research (no automated validator known) | U3 |

No automated test suite applies — this plan ships markdown and JSON configuration, not application code.

## Definition of Done

- `plugin/.claude-plugin/plugin.json` passes `claude plugin validate ./plugin --strict` with zero warnings; `.claude-plugin/marketplace.json` resolves cleanly via `claude plugin marketplace add <local path>` (the validate command is scoped to `./plugin` and does not cover the repo-root marketplace file).
- `plugin/.codex-plugin/plugin.json` includes `name`, `version`, and `description`; `.agents/plugins/marketplace.json` resolves cleanly via `codex plugin marketplace add <local path>`.
- The plugin installs locally via both `/plugin marketplace add` + `/plugin install` (Claude Code) and `codex plugin marketplace add` (Codex), and both skills appear with the correct frontmatter `name`.
- `flowline` correctly detects `.flowline` presence/absence in a scratch project and proposes the right next command; does not declare a `push` behaviorally verified on exit code 0 alone; applies the authority rule when DEV has un-synced changes.
- `flowline-migration` detects at least one of the four migration-source signatures in a fixture and offers migration unprompted; stays silent on a repo that already has `.flowline` plus a stale migration-source file (no false-positive re-offer); its verify step reports failure honestly against a deliberately-incomplete fixture, including partial failures where only one of `push --dry-run` / `sync` fails.
- No scratch/fixture repos used for manual verification are committed to the Flowline repo.
