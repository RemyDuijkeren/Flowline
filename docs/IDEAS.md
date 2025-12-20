# Prefer Unmanaged

Using unmanaged in all environments (your choice) — implications and guardrails.

What you gain:
- Simplicity: one solution type everywhere.
- Easy hotfixes: you can import the same unmanaged solution in any env.
- No managed/unmanaged split to explain to the team.

Real risks to be aware of:
- Environment drift: people can change things directly in DEV/STAGING/PROD, and those edits won’t live in source control unless you re-export and commit.
- Harder rollbacks: no clean uninstall or stage-for-upgrade paths.
- Layering surprises: unmanaged tweaks outside your solution can override your intent.
- Audit/traceability: tougher to prove exactly what changed and when if folks edit in-place.

Guardrails that keep unmanaged workable:
- Source of truth policy: code (unpacked solution in Git), not PROD.
- No in-app editing in shared envs: restrict maker rights in DEV/STAGING/PROD so only your pipeline (or a release manager) can import the unmanaged solution.
- Preflight drift checks in your CLI:
  - Export target env’s solution before import and diff against your artifact.
  - Block the deploy if target contains components modified outside your solution (or not present in Git).
- Version every import: increment solution version on every promote; log who/when/commit SHA.
- Regular refreshes: copy PROD → STAGING (and occasionally PROD → DEV) to reset drift and test with realistic data.
- Config isolation: use Environment Variables and Connection References so config isn’t hand-edited into components.
- Audit & Alerts: enable auditing for customizations; optionally have your CLI warn if the Default Solution has recent changes.


# Flowline — Opinionated Dataverse ALM (Unmanaged Everywhere)

Small‑team friendly (1–5 makers).

**Main path:** DEV (shared Sandbox) → STAGING → PROD.

**Fast path:** DEV (shared Sandbox) → PRODUCTION

**Developer environments are optional** for personal tryouts when you have a clean solution setup. Ships **unmanaged** solutions end‑to‑end with built‑in guardrails.

```
DEV (sandbox, shared)  ──▶  STAGING (sandbox, UAT)  ──▶  PROD (production)
     integrate                    preview/validate                 live

Developer (optional, personal tryout)  ──▶  DEV (sandbox, shared)
```

DEV: Developers build and test their work.
STAGING: Business users and power users validate features in a safe, realistic environment.
PRODUCTION: Final version for all end-users.

---

## Core Principles

* **Unmanaged everywhere.** No managed/unmanaged split to confuse the team.
* **Developer envs optional.** You can skip tryouts and run the mainline only (DEV → STAGING → PROD).
* **One command per phase.** Flowline orchestrates import/export, checks, Git, and gates.
* **Human approvals where they matter.** UAT approval before production.
* **Config isolation.** Environment Variables + Connection References are synced; no hand‑edits.

---

## DEV‑first Commands (Primary Path)

### 0) `flowline init`

Workspace bootstrap: auth, environment IDs, and repo setup.

**Does:**

* Checks prerequisites (Git, PAC, dotnet) and prompts sign‑in if needed.
* If not a Git repo: `git clone <repo>` (or guide if repo already exists).
* Creates/updates `.flowline.yml` (stores env IDs for `dev`, `staging`, `prod`; sets `unmanagedOnly: true`).
* Validates access to **DEV/STAGING/PROD**; warns if DEV is missing.
* No environment copies by default (use `prime` for PROD→DEV).

### 1) `flowline prime`

One‑time bootstrap: clone **PROD** to **DEV**.

**Does:**

* Copies from **PROD → DEV** (choose `customizations` or `everything`).
* Sets solution version baseline and writes a bootstrap tag in Git.

### 2) `flowline push`

Push built assets into **DEV** (never STAGING/PROD).

**Does:**

* Registers/updates **plugin assemblies** and steps.
* Uploads **web resources** (bundled/minified if configured) and publishes customizations.
* Deploys **PCF** components for testing.
* Skips solution export; this is a fast **local → DEV** loop.
* Assumes assets are already built (dotnet CLI or Visual Studio/Code). Use `--build` if you want flowline to run builders for you.

> After `push`, run `export` (or just **`flowline sync`**) to persist the DEV state to Git.

### 3) `flowline export`

Store the current **DEV** changes to Git and (optionally) open a PR.

**Does:**

* Exports solution from **DEV** → unmanaged artifact.
* Unpacks to `/solutions/<SolutionName>`; normalizes files.
* Commits & pushes to the current branch using Conventional Commit message.
* Optional: `--no-pr` to skip opening a PR.

**Tip:** Typical local-code loop → **`flowline sync`** (push + export)

---

### 4) `flowline sync`

One-liner convenience for the DEV loop — runs **`push` → `export`**.

**Does:**

* Validates you’re targeting **DEV** (never STAGING/PROD).
* Optionally builds assets with `--build`; otherwise assumes you built via dotnet/VS/VS Code.
* Executes `push` (plugin assemblies, web resources, PCF), then `export` (unpack, commit, push).
* Supports `--message`, `--no-commit`, `--no-push`, `--no-pr` flags; passes them through to `export`.

### 5) `flowline stage`

Promote **DEV** to **STAGING** for customer review.

**Does:**

* If no `--artifact` is supplied, **exports from DEV** automatically and imports into **STAGING** (unmanaged).
* (Optional) Offer **PROD → STAGING** refresh first (customizations/everything).
* Sync Environment Variables & Connection References (DEV → STAGING).
* Run UAT suite; generate tester links/checklist.
* Wait for **UAT approval** before release.

## Optional Path — Developer Tryout & PR‑first

### 1) `flowline draft "<feature-name>"` (Optional: Developer Tryout)

Creates a feature branch and prepares your workspace. If **Developer tryout** is enabled, it also preps the **Developer** environment and imports the baseline.

**Does:**

* Creates/checks out Git branch `feat/<feature-name>` and pulls `main`.
* Fetches the current solution artifact (from repo or shared DEV).
* If tryout is enabled, imports **unmanaged** solution into **Developer**.
* Prints deep links to test quickly.

*Tip:* enable tryout via `.flowline.yml` (`useDeveloperTryout: true`) or run `flowline draft --tryout`.

---

### 2) `flowline submit` (Optional: Developer Tryout)

Exports work from **Developer** (when tryout was used), unpacks to source, commits, pushes, and opens a PR.

**Does:**

* Export from **Developer** → **unmanaged** artifact.
* Unpack to `/solutions/<SolutionName>` and normalize.
* Commit using Conventional Commits; push branch.
* Open a Pull Request with auto‑filled title/body.
* Attach Solution Checker report to the PR.

---

### 3) `flowline integrate`

Integrates merged PR into **DEV** (shared sandbox).

**Does:**

* Build artifact from `main`.
* Quick environment & solution checks on **DEV**.
* Import to **DEV** (unmanaged) and publish.
* Run smoke tests + Solution Checker; report status.
* Auto bump solution version (patch) and update CHANGELOG.

---

### 6) `flowline release`

Deploys to **PROD** with backups and final checks.

**Does:**

* Verify **UAT approval** is present (unless overridden).
* Create **PROD** customization backup snapshot.
* Final configuration verify on **PROD**.
* Import artifact (unmanaged) to **PROD**; publish.
* Tag Git release (`vX.Y.Z`), update CHANGELOG & PR with release notes.

---

### 6) Hotfix

```
flowline patch "<hotfix-name>"
# …fix in Developer…
flowline release
```

**Start** exports PROD baseline, imports to **Developer**, and creates `hotfix/<hotfix-name>` branch.
**Ship** runs guarded promotion **DEV → STAGING → PROD**, then tags `vX.Y.Z+hotfix.N`.

---

## One Config to Rule Them All — `.flowline.yml`

```yaml
# Minimal starter for Flowline v1
solution: AutomateValue.Core
artifactDir: ./artifacts
unmanagedOnly: true

environments:
  dev:       <envId-dev>       # shared sandbox (integration)
  staging:   <envId-staging>   # UAT sandbox
  prod:      <envId-prod>      # production

bootstrap:
  devCopyMode: customizations   # default for `flowline prime` (customizations|everything)

policies:
  requireUATApproval: true
  backupBeforeProd: true

git:
  mainBranch: main
  releaseTagPrefix: v

configSync:
  from: dev
  to: staging
  include: [EnvironmentVariables, ConnectionReferences]

tests:
  smoke: tests/smoke.yml
  uat:   tests/uat.yml
```

## Opinionated Behaviors

* **Unmanaged‑only imports.** Warn/fail if a managed file is passed.

* **Enforce no in‑place edits** in shared envs (optional: auto‑adjust maker roles).

* **Auto‑versioning.** Patch bump on `integrate`; optional `--minor` on `release`.

* **UAT gate.** `release` blocks until PR has `uat-approved` (or explicit override).

* **Backups.** `release` snapshots PROD before import.

* **Changelog.** Generated from Conventional Commits since last tag.

---

## Example Happy‑Path Session

```bash
# One‑time bootstrap
flowline init                          # setup auth & .flowline.yml
flowline prime --mode customizations   # clone PROD → DEV

# Day‑to‑day (work directly in DEV)
# If you have local code assets, build them with dotnet/VS/VS Code, then:
flowline sync                             # push assets to DEV and export to Git (one-liner)

# Send to customer for review
flowline stage                            # DEV → STAGING for customer review

# After customer sign-off
flowline release                             # deploy to PROD
```

## Overrides & Safety Switches

* `--force` — bypass non‑critical warnings.
* `--dry-run` — show plan; no side effects.
* `--no-open` — suppress browser opens (PR/UAT pages).
* `--minor` / `--patch` — control version bump on release.

**Exit codes**

* `0` Success
* `20` Tests failed
* `30` Missing UAT approval
* `40` Auth/Env config missing

---

## Bootstrap & Status

```bash
flowline init            # guided setup: auth, env IDs, writes .flowline.yml
flowline prime        # clone PROD → DEV (customizations|everything)
flowline status          # env health, solution versions
flowline refresh uat     # prompt PROD→STAGING copy with safeguards
```

---

## Environment Terminology (for docs/help)

* **Developer** (optional) = personal tryout environment (Dataverse *Developer* type).
* **DEV** = shared integration sandbox (Dataverse *Sandbox*).
* **STAGING** = UAT sandbox (Dataverse *Sandbox*; often refreshed from PROD).
* **PROD** = live environment (Dataverse *Production*).
