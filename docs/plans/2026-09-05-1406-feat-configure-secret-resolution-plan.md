---
title: Configure Secret Resolution - Plan
type: feat
date: 2026-09-05
topic: configure-secret-resolution
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Configure Secret Resolution - Plan

## Goal Capsule

- **Objective:** A team can commit an environment's settings file to a shared repository without committing any secret it holds, and can set a plugin step's secure configuration from that file.
- **Product authority:** This document, for secret handling only. The command surface, the settings file's role, and the apply and export semantics belong to [`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md).
- **Open blockers:** Two, both in Outstanding Questions — what export does with a value that may be a secret, and how the set of referenceable variables is restricted.

---

## Product Contract

### Summary

Let any value in a settings file be written as a reference that Flowline resolves at apply time, so the file names a secret without containing it. Add plugin step secure configuration as a fifth component class, writable from the file but never readable back.

### Problem Frame

The configure command's settings file holds literal values, and it is meant to be committed. That is fine for a flow's active state and for most environment variables, and it is not fine for the values that carry credentials.

Three paths carry them. A Dataverse environment variable of type Secret is safe on its own — it stores a Key Vault reference, not the value ([Microsoft Learn](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables-azure-key-vault-secrets)). A plain-string environment variable is not: small teams put secrets there because Key Vault setup is disproportionate for them, and that is an accepted practice this project supports. A plugin step's secure configuration holds a literal secret with no indirection available at all.

Sensitivity is therefore a property of an individual value, not of a component class. Nothing in the data distinguishes a plain-string variable holding an API key from one holding a region code, so no rule keyed on type can protect the first without breaking the second.

Flowline already resolves one secret this way. `SecretResolver` takes the client secret from a flag, then `AZURE_CLIENT_SECRET`, then an interactive prompt, and fails with a typed exit code otherwise, never logging the value (`src/Flowline/Services/SecretResolver.cs:20-39`). That precedence is the right shape; it resolves exactly one fixed name and returns one value, so it is a precedent rather than a component to extend.

<!-- ce-section: work-relationships -->
### How This Work Fits Together

This plan owns **secret handling for the settings file**. It has no command surface of its own.

- **The configure command** ([`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md)) — this plan *Depends on* it and must not restate it. That plan's R4 states the exposure this one removes, so the two are worth sequencing close together: every settings file written before this plan lands holds literal values, and rewriting committed files is not the same as never having written them.
- **Deploy integration with pre-import validation** — *Depends on* both plans. The reason a deploy hook is worth its cost is that it can resolve and validate before packing, which needs the resolution pass R3 defines.

### Key Decisions

- **Indirection is a property of the value, not of the section.** Any value anywhere in the file may be a reference, because a plain-string environment variable can carry a secret while a Secret-type one carries only a Key Vault reference. Sorting by component class would be wrong in both directions. Governs R1.
- **`dotnet user-secrets` is a resolution rung, bought for ergonomics rather than security.** It is unencrypted plaintext in the user profile, so against an environment variable it buys persistence across shells and per-solution scoping, not protection. (session-settled: user-directed — chosen over environment variables alone and over native Key Vault resolution: the small-team case needs persistence without Key Vault setup.) Governs R2.
- **Small teams may keep secrets in plain-string environment variables.** Accepted risk: readable by any sufficiently privileged user, mitigated by admin-only environment access. This is why the design cannot infer which values are sensitive. (session-settled: user-directed — chosen over requiring Secret-type variables: Key Vault setup is disproportionate for a small team.) Governs R1.
- **This is a new resolver, not one more rung on the existing one.** The precedent resolves a single fixed name; this resolves arbitrarily many author-named variables with a per-variable typed failure.
- **Secure configuration is apply-only.** Dataverse never returns it on read (`CONCEPTS.md:86`), so it can be written from a file and never captured into one. Governs R8, R9.

### Requirements

**Referencing a secret**

- R1. Any value in the settings file may be written as a reference instead of a literal, and Flowline resolves it when the file is applied. A file may mix references and literals freely.
- R2. Resolution order is: command-line flag, then process environment variable, then `dotnet user-secrets`, then a masked interactive prompt, then failure. This follows the precedence `SecretResolver` establishes (`src/Flowline/Services/SecretResolver.cs:20-39`).
- R3. Every reference in the file resolves before the first write. An unresolvable reference fails the command with a typed exit code and applies nothing, so a missing secret can never leave an environment half configured.
- R4. Flowline never writes an empty value in place of a missing secret.
- R5. Only variables the settings file may legitimately need are resolvable. A reference to a variable outside that set fails rather than resolving, so a change to a committed file cannot read an unrelated secret out of the process environment.

**Keeping a resolved value contained**

- R6. A resolved value never reaches an output surface: not the log, not `--dry-run` output, not change summaries, not failure messages, not verbose output, not captured PAC stderr, and not telemetry. A diff reports a value as changed or unchanged without printing it.
- R7. A resolved value is never written to any file, including the settings file it came from and any temporary file. Where a downstream tool requires one, it is created outside the working tree with owner-only permissions and removed on every exit path, including failure and cancellation.

**Plugin secure configuration**

- R8. The settings file may declare a plugin step's secure configuration, applied like any other component and subject to R1 through R7. It applies within the plugin step tier of the configure plan's apply order and before the step's enabled state, so a step is never enabled ahead of the configuration it needs.
- R9. Export never emits secure configuration values, because Dataverse does not return them. An exported file names the steps that have one and states that the values must be supplied before the file can be applied.

### Key Flows

**F1 — Commit a settings file that names a secret**

1. Operator exports a settings file and replaces the sensitive values with references (R1).
2. Operator sets each referenced value locally through `dotnet user-secrets` (R2) and commits the file.
3. The committed file names every secret and contains none.

**F2 — Apply in CI**

1. The pipeline provides each referenced value as a process environment variable.
2. Flowline resolves every reference before writing anything (R3), failing the run if one is missing (R4).
3. Values are applied; none appears in the build log (R6).

### Acceptance Examples

- AE1. A file references a variable that is set nowhere in the chain, and the run is non-interactive. The command fails with a typed exit code, names the unresolved variable, and applies nothing at all — including the components whose values did resolve. Covers R3, R4.
- AE2. A dry-run over a file whose environment variable value is a reference names the variable, reports that it would change, and the resolved value appears nowhere in the output. Covers R6.
- AE3. A file references the variable holding the pipeline's own service principal secret. The command fails rather than resolving it. Covers R5.
- AE4. A file is exported from an environment whose plugin steps carry secure configuration. The exported file names those steps without values and states that they must be supplied before it can be applied. Covers R9.
- AE5. A run is cancelled midway while a temporary file holds resolved values. No such file remains afterwards, in the working tree or outside it. Covers R7.

### Scope Boundaries

Deferred for later:

- Native Azure Key Vault resolution inside Flowline. A pipeline step that stages a Key Vault secret into an environment variable already covers the CI case, and native resolution adds an Azure SDK dependency and a second auth path.

Outside this work:

- Anything the configure command plan owns: the command surface, discovery, apply ordering, export scoping, exit codes.
- `.env` file support. Azure's own guidance is not to store secrets in one, and it places a live secret inside the working tree.
- Protecting a secret already inside Dataverse. A value in a plain-string environment variable is readable by privileged users, and Flowline gains no ability to detect or protect it.

### Dependencies / Assumptions

- Depends on the configure command plan, which ships first and on its own. This plan changes how values are supplied; it does not supply the command that applies them. Settings files written before it lands hold literal values, and rewriting a committed file is not the same as never having written one — a team adopting both in sequence rotates the secrets exposed in between.
- Assumed: the `dotnet user-secrets` store is keyed by an id Flowline owns, not by a `UserSecretsId` added to the PAC-managed `.cdsproj`, which is never hand-edited. Where that id lives is a planning decision.
- The settings file becomes a privileged input executed against production, so repository write access, branch protection, and review on that file become production access controls. Teams should treat it as such.
- The masked prompt rung means an operator can type a production secret into a terminal, where scrollback is outside Flowline's control. Whether the prompt fires per unresolved variable, or is suppressed once a file declares more than one, is a planning decision.

### Outstanding Questions

**Resolve Before Planning**

- What does export do with a value that may be a secret? Four reviewers found the underlying problem: a plain-string value is indistinguishable from an ordinary one on read. Either export writes it verbatim and warns, naming every literal written, or export emits a reference placeholder by default with an opt-in flag for literals. The first keeps export honest about what the environment holds; the second makes the safe path the default and the unsafe one deliberate.
- How is the R5 restriction expressed — an allowlist in `.flowline`, a required naming prefix, or a refusal list of known credential variable names? An allowlist declared in the settings file itself is ruled out: it lives in the artifact R5 defends against, so one commit can widen the file and its own permission together. Whichever is chosen must also bind a variable to the component it may be resolved for: R5 as written guards the read side only, so an edit could move a permitted reference onto a different component that exposes the value. Each remaining option has a different failure mode when a team adds a legitimately new variable.

**Deferred to Planning**

- The reference syntax itself, and how a literal value that happens to look like a reference is escaped.
- Whether the masked prompt fires per variable or is suppressed for multi-reference files.

### Sources / Research

- `src/Flowline/Services/SecretResolver.cs:20-39` — the precedence this plan generalizes.
- `CONCEPTS.md:86` — secure configuration is never returned on read and is excluded from solution export.
- [Use environment variables for Azure Key Vault secrets](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables-azure-key-vault-secrets) — Secret-type variables store references, not values; Key Vault is the only supported store.
- [`docs/brainstorms/2026-06-12-environment-config-command-requirements.md`](../brainstorms/2026-06-12-environment-config-command-requirements.md) — the original secure-config proposal, superseded here.
