---
title: Run Flowline commands from any subdirectory
date: 2026-06-29
status: ready-for-planning
---

## Problem

Flowline commands require the working directory to be the project root (where `.flowline` lives).
Developers deep in a subdirectory must `cd` back to the root before running any command — a friction point that breaks flow.

## Outcome

Flowline commands work from any directory at or below the project root, without the user needing to change directory.

## Behavior

**Root discovery:**  
On every command invocation, Flowline walks parent directories from the current working directory upward until it finds a `.flowline` file or reaches the filesystem root. The directory containing `.flowline` becomes the project root for that invocation.

**Path resolution:**  
Unchanged. Relative paths in command arguments always resolve from the discovered project root, not from the directory the command was run in. This matches current behavior when already at the root.

**No project found:**  
When no `.flowline` is found walking up, Flowline exits with a clear message:

> No Flowline project found. Run `flowline clone` to set up a project.

**Nested projects:**  
Not supported. The first `.flowline` found walking up is used. Multiple nested `.flowline` files are out of scope.

## Implementation anchor

`FlowlineCommand.RootFolder` (currently `Directory.GetCurrentDirectory()`) is replaced with a walk-up method in the base class. All commands inherit the fix automatically. No command-level changes needed.

This is the only targeted change — no `--root` override flag, no config-level changes.

## Out of scope

- `--root <path>` flag
- `flowline init` command
- Nested / multi-project layouts
