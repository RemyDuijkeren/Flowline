# Flowline Brainstorm

## Core Thesis

Flowline brings Git, CI, and repeatable delivery to unmanaged Dataverse solutions.

This is the main reason the project exists.

Flowline is not meant to be just another PAC CLI wrapper or a generic pipeline generator.

Its value is to encode a strong, opinionated Dataverse delivery model for small teams:

- unmanaged-first
- repo-first
- trunk-based by default
- agent-friendly
- simple by default, stricter when needed

## Positioning

Flowline is a terminal-first Dataverse delivery tool for small teams and AI agents.

It should:

- optimize for small teams, not large implementation partners or enterprises
- prefer unmanaged solutions by default
- avoid unnecessary ceremony
- support Git, CI, validation, and repeatable deployment
- make professional unmanaged delivery feel normal

## Product Principles

### Unmanaged Can Be Professional

Unmanaged solutions should be treated as a legitimate default for serious small-team delivery.

### Source Control Is The Source Of Truth

Even when work starts in Dataverse, the repository should be the preferred release source and shared record.

### Avoid Long-Running Divergence

This is a core mantra for Flowline.

Flowline should help teams:

- keep environments close to source control
- reduce the lifetime of divergent states
- refresh or reprovision workspaces before they become untrustworthy
- avoid branch and environment sprawl

### Simplicity Is A Feature

Small teams should not be forced into enterprise ceremony by default.

### Stricter Controls Should Remain Available

Managed solutions, PR workflows, staging, and stronger governance may be supported, but they should not define the product identity.

### Integrate Early, Activate Deliberately

Flowline should fit a feature-flag-friendly way of working:

- integrate changes early
- keep `main` moving
- deploy safely
- activate behavior deliberately

## Working Model

### Default Direction

The default Flowline experience should be:

- unmanaged-first
- Git repo required
- repo-sourced deployment preferred
- trunk-based by default
- simple for small teams
- CI and validation friendly

### Deployment Source

Flowline should be repo-sourced first.

That means:

- sync changes from Dataverse into source control
- package from source control
- deploy from source control

Environment-sourced deployment can remain a possible future fallback, but should not be a first-class v1 workflow.

### Branching

The default should be trunk-based development.

Branches should be supported for temporary isolation only:

- `feature/*` for larger or riskier work
- `hotfix/*` for urgent fixes based on production truth

Long-lived `dev` or `staging` branches should not be encouraged by default.

### Environments

Dataverse environments should be treated as workspaces, not permanent truth.

Recommended model:

- `prod` is the live baseline
- `dev` is the main working workspace
- `staging` is optional
- `dev` and `staging` are disposable and can be reprovisioned from `prod` when needed

Because Dataverse environment creation is relatively heavy, these workspaces may live longer than a Git feature branch, but the intent is the same:

- isolate when needed
- reintegrate quickly
- avoid long-running divergence

## Delivery Philosophy

Flowline should optimize for continuous integration by avoiding long-running divergence.

This means:

- `main` stays close to deployable
- every commit should be a potential production candidate
- branches are exceptions, not the normal path
- PRs are optional governance, not required professionalism
- environments should converge back to the main line quickly

## Managed, PRs, And Scope

### Managed

Managed solutions may be supported, but they should remain secondary to the unmanaged-first default.

### PRs

PRs may be supported, but should not be the core story.

Good framing:

- not anti-PR
- not PR-first
- PR-compatible

### AI Context

AI reduces the value of generic pipeline generation.

That makes Flowline more valuable when it acts as:

- a Dataverse-aware workflow layer
- a safety and policy layer over PAC
- a predictable contract that humans and agents can both use

PAC provides mechanics.

Flowline should provide:

- workflow
- safety
- defaults
- guardrails
- recovery paths
- environment roles
- Git-aware discipline

## What Flowline Should Be Good At

Flowline should focus on:

- sync between Dataverse and source control
- repo-first packaging and deployment
- drift detection
- safe environment lifecycle management
- actionable diagnostics
- machine-readable output for agents and CI

## Baseline Tracking

Flowline should clearly mark what commit is currently represented in production and staging.

Recommended approach:

- use immutable annotated Git tags for real deployments
- optionally keep moving convenience markers for current prod and staging baselines

Examples:

- `prod/2026-04-05.1`
- `staging/2026-04-05.1`
- `prod/current`
- `staging/current`

Immutable tags are the real deployment history.

Moving markers are convenience pointers.

This helps answer:

- which commit matches production right now?
- where should a hotfix branch start from?
- what changed since the last production deployment?

## Git Boundary

Flowline should not create Git commits or push to remotes as part of its core command model.

Reason:

- Git already owns source control workflow
- Flowline should stay focused on Dataverse workflow and delivery discipline
- automatic commit and push behavior hides important user intent

Instead, Flowline should enforce Git guardrails at sensitive boundaries.

Recommended behavior:

- `push` and `sync` may run while Git work is still in progress
- `deploy` should require healthy Git state by default

Healthy Git state can include:

- no uncommitted changes
- no unstaged changes
- no unpushed local commits when a tracking branch exists

## CLI Model

The CLI should reflect the repo-first unmanaged workflow instead of older branch or PR metaphors.

### Proposed V1 Command Surface

- `clone`
- `new`
- `push`
- `sync`
- `status`
- `drift`
- `provision`
- `deploy`

### Command Philosophy

Functional consultant pattern:

- work in `dev`
- run `sync` when ready

Technical consultant pattern:

- work locally on assets
- run `push` during iteration
- optionally make additional changes in `dev`
- run `sync` when ready

Safety pattern:

- when extra confidence is needed, run `push` followed by `sync` before packaging or deployment

Delivery pattern:

- `deploy` is the delivery primitive

## Command Roles

### `clone`

Use for bootstrapping an existing solution into the repo.

Behavior:

- production-only baseline for now
- verify current folder is already a Git repo
- clone the solution locally
- create or update `.flowconfig`

Default safety:

- if the solution is already configured or already cloned, stop by default
- require `--force` to re-bootstrap
- if `--prod` differs from configured `ProdUrl`, stop by default

`clone` may omit `--prod` only when `.flowconfig` already exists and contains `ProdUrl`.

### `new`

Use for creating a brand new unmanaged solution in `dev`.

Behavior:

- create the solution in `dev`
- bootstrap it locally into the repo
- create or update `.flowconfig`
- set it as default when appropriate

`new` is for greenfield solutions.

`clone` is for bringing an existing solution under Flowline control.

### `push`

Use to push local technical assets into the `dev` environment.

This is mainly for developers and technical consultants.

`push` is a dev-only workflow and should reject staging or production roles.

### `sync`

Use to synchronize the current `dev` environment state back into the repo.

This is the main checkpoint command for both functional and technical work.

### `status`

Should show:

- configured environments
- current repo state
- whether `dev` appears dirty or unsynced
- whether the repo looks ready for packaging
- whether `dev` is safe to reprovision

### `drift`

Use to compare expected and actual states.

Examples:

- repo to dev
- dev to prod
- repo to target environment

### `provision`

Use to provision a real `dev` or `staging` environment from production.

This command is intentionally heavy and potentially destructive.

It can:

- create the workspace environment if it does not exist
- overwrite an existing environment when explicitly allowed

This replaces the earlier `prime`/`refresh` naming.
`provision` is preferred because it communicates full environment setup, not a lightweight refresh.

### `deploy`

Core primitive:

- package from source control
- import into a target environment

## Parameter Model

The parameter model should stay opinionated and small.

Principles:

- use `.flowconfig` as the default source of repeated values
- do not support overriding the config file path
- keep unmanaged as the implicit default everywhere
- use explicit flags only when deviating from the default workflow
- prefer clear role-specific environment flags over generic `--environment`

### Global Parameters

Recommended global flags:

- `-v, --verbose`: show more diagnostic output
- `--json`: output machine-readable JSON for agents and CI
- `--yes`: accept confirmations automatically for non-interactive runs
- `--solution <name>`: override the configured default solution for the current command

Do not add:

- `--config <path>`
- generic `--environment <url>`

### Environment Flag Naming

Preferred names:

- `--source <...>`: source side of a comparison-style operation
- `--target <...>`: target side of a deployment-style operation
- `--dev <url>`: development environment override
- `--staging <url>`: staging environment override
- `--prod <url>`: production environment override

Guideline:

- use `--target` for delivery-oriented commands such as `deploy`
- use role-specific flags like `--dev` for dev-only commands such as `push`

### Managed Flag Model

Flowline is unmanaged-first.

That should be reflected directly in the CLI:

- unmanaged is the default and requires no flag
- `--managed` is the explicit override

Interpretation:

- `clone`: also clone managed artifacts
- `sync`: also sync managed artifacts
- `deploy`: deploy the managed package

Constraint:

- if the repo was only cloned or synced as unmanaged, managed deployment is not possible

## Per-Command Parameter Proposals

### `clone`

Preferred shape:

- `flowline clone <solution>`

Parameters:

- `<solution>`: solution to bootstrap
- `--prod <url>`: production baseline to clone from
- `--managed`: also clone managed artifacts
- `--output <path>`: override the local output folder
- `--dev <url>`: save the development environment URL into `.flowconfig`
- `--force`: allow re-bootstrap when local/configured state already exists

### `new`

Preferred shape:

- `flowline new <solution>`

Parameters:

- `<solution>`: unique solution name to create and track
- `--dev <url>`: development environment where the solution should be created
- `--publisher <prefix>`: publisher prefix for the new solution
- `--display-name <text>`: optional friendly solution display name
- `--output <path>`: override the local output folder
- `--managed`: also include managed artifacts during bootstrap
- `--force`: allow overwrite of conflicting local bootstrap state

### `push`

Preferred shape:

- `flowline push`
- `flowline push <solution>`

Parameters:

- `<solution>`: optional solution override when multiple solutions exist
- `--dev <url>`: override the configured development environment
- `--assets <all|webresources|plugins|pcf>`: limit the push scope
- `--force`: bypass lightweight safety checks

### `sync`

Preferred shape:

- `flowline sync`
- `flowline sync <solution>`

Parameters:

- `<solution>`: optional solution override when multiple solutions exist
- `--dev <url>`: override the configured development environment
- `--managed`: also sync managed artifacts
- `--force`: continue even when Flowline detects non-blocking sync warnings

### `status`

Preferred shape:

- `flowline status`

Parameters:

- `--dev <url>`: inspect status against a specific development environment
- `--target <role|url>`: include status for a specific target environment
- `--detailed`: show expanded diagnostics

### `drift`

Preferred shape:

- `flowline drift <source> <target>`

Parameters:

- `<source>`: source side of the drift comparison
- `<target>`: target side of the drift comparison
- `--solution <name>`: limit the drift check to a specific solution
- `--detailed`: include more detailed differences
- `--fail-on-drift`: return a failing exit code when drift is detected

### `provision`

Preferred shape:

- `flowline provision <dev|staging>`

Parameters:

- `<dev|staging>`: choose whether to provision `dev` or `staging`
- `--prod <url>`: production baseline used for provisioning
- `--suffix <suffix>`: override the default suffix used to derive the target URL
- `--copy <minimal|full>`: choose the Dataverse copy type
- `--allow-overwrite`: explicitly allow overwriting an existing target environment

Provisioning defaults:

- `dev` => `minimal`
- `staging` => `full`

Provisioning rules:

- `.flowconfig` should be updated implicitly after success
- if `--prod` is omitted, Flowline should use `ProdUrl` from `.flowconfig`
- if `--prod` differs from configured `ProdUrl`, Flowline should stop by default
- target URL should be derived from the production URL plus the role or suffix, not accepted as an arbitrary full URL

### `deploy`

Preferred shape:

- `flowline deploy <target>`

Parameters:

- `<target>`: destination environment or role such as `prod`, `staging`, or an explicit URL
- `--solution <name>`: solution to package and deploy
- `--managed`: deploy the managed package instead of unmanaged
- `--artifact <path>`: deploy a prebuilt package
- `--fail-on-warnings`: treat warnings as blocking failures

## Multi-Solution Support

Flowline should eventually support multiple related solutions in one repo.

Recommended direction:

- keep single-solution UX as the default
- support multiple configured solutions explicitly
- allow one default solution
- later support grouped deployment or dependency ordering if needed

## Isolation And Hotfixes

Flowline should support isolated work as an exception path, not as the default model.

Recommended rules:

- isolated work should start from production truth, not from a dirty shared dev workspace
- isolated work should record the production-aligned base commit
- isolated changes should reintegrate through Git first, then environment reconciliation

For hotfixes:

1. create a temporary environment from production
2. optionally create a temporary `hotfix/*` branch from the production-aligned commit
3. make the fix
4. sync into Git
5. deploy to production
6. merge back into `main`
7. reconcile shared `dev` from updated Git state

Direct fixes in production should be treated as recovery scenarios, not normal workflow.

Recommended recovery rule:

- recover prod changes into Git first
- merge them into `main`
- then reconcile or reprovision `dev`

## Open Questions

1. What exact problem should Flowline own that Azure DevOps, GitHub Actions, and Dataverse Pipelines do not?
2. What is the smallest command surface that still makes Flowline useful to both humans and agents?
3. Which capabilities belong in the core product, and which should stay in generated CI examples?
4. What does "safe enough for AI agents" mean in practice for Dataverse deployments?
5. Where should Flowline be intentionally opinionated, and where should it stay flexible?
