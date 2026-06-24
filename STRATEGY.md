---
name: Flowline
last_updated: 2026-06-23
---

# Flowline Strategy

## Target problem

Unmanaged Dataverse solutions have no credible ALM tooling, which pushes teams
toward managed solutions they don't need — adding complexity small teams can't
absorb. Teams that resist the pressure are left with ad-hoc processes: no source
control discipline, no repeatable delivery, and no guardrails when environments
drift.

## Our approach

Flowline bets on PROD as the canonical baseline and Git as the delivery source —
dev and test environments are short-lived workspaces provisioned from PROD, not
permanent masters. This is the opposite of the ISV golden-env model, and is
designed specifically for teams extending their own Dataverse environment.

**Environment-centric, not source-centric.** DEV is the working canvas — makers and developers
contribute directly in the environment, `sync` captures that state into source
control, and `deploy` packs from `src/`, shipping exactly what was confirmed in
DEV. This is deliberately not the ISV pack-flow where source builds produce the
shippable artifact. For ISV-style reproducible builds (no shared DEV env,
source-as-truth, AppSource distribution), use ALM Accelerator or Power Platform
Build Tools. Flowline makes no attempt to serve that model.

**Progressive adoption.** `push` and `generate` work standalone — no `.flowline`
config required. A consultant can start with `flowline push` on an existing client
project without committing to the full workflow; adopt `clone`, `sync`, and
`deploy` when ready. Low adoption friction is a deliberate design choice.

## Who it's for

**Primary:** Solo or small-team technical consultant — they're hiring Flowline to
bring source control and a repeatable delivery workflow to a client's Dataverse
environment that would otherwise be managed ad-hoc.

**Secondary:** In-house IT developer doing occasional Dataverse customization —
not full-time on Dataverse, wanting structure without enterprise ceremony.

## Key metrics

- **Personal use** — Remy actively uses it on client work; first signal that it's
  good enough to eat your own cooking
- **NuGet downloads** — lagging adoption signal; measured on NuGet.org
- **GitHub stars** — awareness signal in the Dataverse community
- **Community posts** — someone writing about unmanaged ALM using Flowline;
  evidence the message is landing
- **Real deployment questions** — issues or discussions showing someone ran a
  full clone→sync→deploy workflow

## Tracks

### Core delivery workflow

Provision, clone, sync, push, and deploy — the complete unmanaged pipeline from
source control to Dataverse.

_Why it serves the approach:_ Without a complete working pipeline, the
PROD-as-truth model has no legs.

### Code asset push

Attribute-driven plugin registration, direct web resource push, and early-bound
type generation — the technical-consultant inner loop.

_Why it serves the approach:_ Technical consultants need a fast path to push code
assets without a full solution import; this makes Flowline practical for daily
work. Type generation removes the last reason to keep spkl installed alongside
Flowline.

### Drift detection + component cleanup

Surface drift between environments, detect deleted components, and eventually
auto-delete on deploy.

_Why it serves the approach:_ Auto-delete is the primary argument for managed
solutions; closing this gap removes the last credible reason to choose managed
over unmanaged.

## Milestones

- **2026-06-04** — Package/ subfolder refactor: PAC-managed files in `Package/Package.cdsproj` ✓
- **2026-06-07** — AI agent improvements: typed exit codes (ExitCode enum), AGENTS.md scaffolding in `flowline clone`, expanded command help text ✓
- **2026-06-10** — Deploy command complete ✓
- **2026-06-15** — Orphan cleanup on deploy (`--no-delete`): auto-delete + report-only classification, cross-solution safety check ✓
- **2026-06-21** — WebResource dependencies push support (`// flowline:depends`, RESX auto-link) ✓
- **2026-06-21** — v0.7.0 released to NuGet ✓
- **2026-06-24** — Migration guide and command reference on GitHub Wiki; README trimmed to overview + quick start ✓
- **2026-06-27** — CHANGELOG with full version history (0.1.0–0.7.0) ✓
- **2026-06-23** — `generate` safe deletion: user-owned files preserved via `IsGeneratorOwned` check during temp-swap ✓
- **2026-06-23** — `provision` region guard: error on config-stored target URL in different region than prod ✓
- **2026-06-24** — `componenttype` constants confirmed via Dataverse `PicklistAttributeMetadata`; low-numbered types are stable platform constants ✓
- **2026-06-26** — `deploy` orphan cleanup integration test: validate AE1–AE8 against real org
- **2026-06-28** — `deploy` pre-backup + `--skip-backup` opt-out (could-have)
- **2026-07-01** — v1.0 release

## Deferred

- `flowline init` for greenfield projects (create publisher + solution in DEV, scaffold local structure) — post-v1
- Restore state of workflows on deploy (`--no-restore` flag; requirements: `docs/brainstorms/2026-06-12-deploy-state-restoration-requirements.md`) — post-v1

## Marketing

**One-liner:** Flowline is a CLI tool for delivering Dataverse solutions with
unmanaged packages, Git as the source of truth, and a straightforward DEV to TEST
to PROD workflow.

**Documentation:** GitHub Wiki live at https://github.com/RemyDuijkeren/Flowline/wiki — command reference, migration guide, and getting-started.
README covers overview, install, and quick start only — links out to the Wiki.
