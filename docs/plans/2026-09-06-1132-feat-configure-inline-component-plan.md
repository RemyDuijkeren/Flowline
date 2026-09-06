---
title: Configure Inline Component Change - Plan
type: feat
date: 2026-09-06
topic: configure-inline-component
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Configure Inline Component Change - Plan

## Goal Capsule

- **Objective:** An operator can turn one flow, workflow or plugin step on or off, or set one environment variable value, in a named environment, in a single command, without editing a file.
- **Product authority:** This document, for the inline surface only. The settings file, the apply path, discovery, ordering and exit codes belong to [`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md).
- **Open blockers:** Component addressing is undesigned. See Outstanding Questions.

---

## Product Contract

### Summary

Extend `flowline configure <env>` to accept one component on the command line instead of a settings file, changing that component's state or value directly.

### Problem Frame

Turning a flow on at go-live, or off during an incident, currently means the maker portal. The configure command reconciles a whole file, which is the wrong shape for a single deliberate change under time pressure: it requires a file to exist, to be current, and to be edited and saved before anything happens.

This was the fourth of four priorities when the configure command was scoped, and it is the only part of that command whose grammar was never settled. It was lifted out of v1 so the rest could be planned without it.

<!-- ce-section: work-relationships -->
### How This Work Fits Together

This plan owns the **inline surface** of `configure`.

- **The configure command** ([`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md)) — *Depends on* it entirely. This plan adds an input shape to a command that plan defines, and reuses its apply path, its exit codes and its stand-alone mode.
- **A read primitive for CI gates** ("is this flow on in PROD, exit 0 or 1") — *Shares* whatever addressing grammar this plan settles. Deciding them together is cheaper than deciding them apart, since a grammar that reads well for a write should read well for a read.

### Key Decisions

- **The inline surface lives inside `configure`.** It is the same reconcile with a smaller input, so it inherits `--dry-run`, the exit codes, and the stand-alone rules. Governs R1.
- **The settings file stays authoritative.** An inline change writes the environment and nothing else; the file is not updated. Governs R4.

### Requirements

- R1. `flowline configure <env>` accepts one component on the command line instead of a settings file, changing that component's state or value.
- R2. Component types covered are flows, classic workflows, plugin steps, environment variable values and connection references — the same classes the settings file covers.
- R3. Setting a state and setting a value are both supported, since two of the five classes carry a value rather than an on-or-off state.
- R4. When the environment's settings file also names the component, the command warns that the next full apply will override the change, naming the file. The file is not modified.
- R5. `--dry-run` reports what would change and writes nothing, as it does for a file apply.
- R6. An address that matches no component fails, and one that matches more than one fails naming the candidates, rather than picking one.

### Acceptance Examples

- AE1. `configure prod` addressing a flow that the PROD settings file declares off turns the flow on and warns that the next full apply will turn it off again, naming the file. Covers R1, R4.
- AE2. The same command with `--dry-run` reports the change and leaves the flow untouched. Covers R5.
- AE3. An address matching two components in the solution fails, names both, and changes nothing. Covers R6.
- AE4. Setting an environment variable value inline changes the value in the target and leaves every other component alone. Covers R2, R3.

### Scope Boundaries

Outside this work:

- Writing the inline change back into the settings file. The configure plan settled that the file is authoritative and an inline change is deliberate drift, surfaced by R4's warning.
- An interactive picker for users who do not know a component's name.
- Reading state for CI gates. It shares this plan's grammar and is listed as a relationship, not as scope.

### Outstanding Questions

**Resolve Before Planning**

- How a component is addressed. Options include a type-and-name pair (`flow "Order Processing"`), a logical or schema name, or a single qualified token. The choice has to work for all five classes in R2, read well under an agent's quoting rules, and extend to the read primitive.
- How a target state or value is expressed. A `--on`/`--off` pair reads well for the three state classes and breaks for the two value classes; a single `--value` reads well for values and awkwardly for states.

**Deferred to Planning**

- Whether display names, logical names, or both are accepted, and how a name containing quotes or spaces is handled.
- Whether a bare component type with no name is a useful form, meaning apply just that section of the file — the `--scope` question the configure plan deferred here.
- Whether an inline change against a component absent from the target is a skip, matching the configure plan, or a failure, since the operator named it explicitly.

### Sources / Research

- [`2026-09-05-1332-feat-environment-configure-command-plan.md`](2026-09-05-1332-feat-environment-configure-command-plan.md) — the command this extends, including the naming decision that kept the inline surface inside `configure` rather than promoting it to `set`/`get`.
- [`docs/ideation/2026-07-01-post-deploy-environment-config-ideation.html`](../ideation/2026-07-01-post-deploy-environment-config-ideation.html) — idea 8 proposes the inline and interactive modes, and is the closest thing to a prior design.
