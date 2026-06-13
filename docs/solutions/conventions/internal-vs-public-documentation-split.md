---
title: Internal vs Public Documentation Split
date: 2026-06-13
category: docs/solutions/conventions/
module: documentation
problem_type: convention
component: documentation
severity: medium
applies_when:
  - Creating or updating wiki pages from internal docs
  - Deciding what detail level belongs in user-facing vs developer documentation
  - Reviewing wiki edits for implementation detail leakage
tags:
  - documentation-structure
  - wiki
  - developer-experience
  - content-organization
---

# Internal vs Public Documentation Split

## Context

Flowline maintains two documentation layers: `docs/` (internal, developer-facing) and the GitHub Wiki (public, user-facing). When an Authentication wiki page was created from `docs/auth.md`, implementation details — specifically the PAC CLI token cache file path — were initially carried over. Removing them surfaced the need for an explicit convention.

## Guidance

**GitHub Wiki** targets end users of the Flowline CLI. Cover workflows, setup steps, and integration guides. Omit internal paths, cache mechanisms, and implementation specifics users have no reason to know.

**`docs/`** targets contributors and maintainers. Include full implementation details: internal paths, architectural decisions, technical specifics needed to understand and extend the codebase.

When content bridges both audiences (e.g., authentication), place implementation details only in `docs/` and surface the user-relevant workflow in the wiki. Where the wiki page is derived from a `docs/` source, link to it for contributors who want the full picture.

## Why This Matters

Users don't need to know where token caches live — they need to know how to authenticate. Exposing implementation details in the wiki:
- Creates false external API contracts (a file path that can change becomes something users depend on)
- Couples wiki maintenance to internal refactors
- Pollutes user-facing docs with noise that doesn't affect their workflows

Maintainers, conversely, do need the full story to debug and extend the system.

## When to Apply

- Creating a new wiki page from an internal `docs/` file: strip implementation details before publishing
- Reviewing a wiki PR or edit: ask "does a user need this to use Flowline?" — if no, move it to `docs/`
- Adding a new `docs/` file: it can freely include paths, class names, and internal mechanics

## Examples

**Token cache path — before (wiki, inappropriate):**
> Flowline reads the PAC CLI token cache from `%LOCALAPPDATA%\Microsoft\PowerAppsCLI\tokencache_msalv3.dat`.

**After (wiki, user-focused):**
> Flowline reads the PAC CLI token cache and acquires a token silently — no browser, no password prompt.

**Implementation detail (stays in `docs/auth.md`):**
> Token cache: `%LOCALAPPDATA%\Microsoft\PowerAppsCLI\tokencache_msalv3.dat`

---

**CI/CD auth — before (Getting-Started.md had a full section):**
```yaml
- run: pac auth create --kind ServicePrincipal --applicationId $CLIENT_ID ...
- run: flowline deploy prod
```

**After:** Brief "Authenticate" blurb in Getting-Started links to `[[Authentication]]`, which covers CI/CD. Duplication removed.

## Related

- `docs/auth.md` — internal auth implementation reference (developer-facing, contains implementation details)
- `Flowline.wiki/Authentication.md` — public auth page (user-facing, no implementation details)
- [ai-agent-consumable-cli-contract](../architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md) — related pattern: stable public surface vs internal variance
