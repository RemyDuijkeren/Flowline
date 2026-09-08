# CLI command surface design review

Date: 2026-09-08
Reviewer stance: senior solution architect, Dataverse ALM, daily CLI user, agent-driven runs.
Scope: commands, arguments, options, defaults, exit codes, help text. Not code quality.
Sources: `README.md`, `STRATEGY.md`, wiki pages 01 to 13, `src/Flowline/Program.cs`, every
`Settings` class under `src/Flowline/Commands/`, `src/Flowline.Core/ExitCode.cs`, and the
`--help` output of a Release build (v0.18.1-alpha).

How to use this file: each finding ends with a `Decision:` line. Fill it with
`accept`, `modify`, `reject` or `defer`, add a note, and the finding becomes a work item or
closes. Nothing here has been changed in code.

Severity: **High** = users or agents get a wrong or destructive result, or the surface can't
grow without a breaking change. **Medium** = friction, surprise, or inconsistency a user has to
learn around. **Low** = polish.

## Summary

| ID | Finding | Severity | Decision |
|---|---|---|---|
| C1 | Three grammars for naming an environment | High | accept, modified |
| C2 | `.flowline` edited only through side effects of other commands | High | modify |
| C3 | Four fixed roles is a ceiling on real team topologies | High | defer |
| C4 | `--skip-*` and `--no-*` both mean "turn a step off" | Medium | reject, rule instead |
| C5 | `provision --allow-overwrite` bypasses the force-specifier pattern | High | accept |
| C6 | `--dry-run` missing on five writers, and deploy's dry run writes a backup | High | reject |
| C7 | Global options crowd every command's help | Medium | accept |
| C8 | PAC auth profile switch prompt defaults to abort | Medium | modify |
| C9 | Exit code 15 overloaded, drift and diff disagree, 130 for a declined prompt | Medium | accept, narrowed |
| C10 | `--client-secret` on the command line | Medium | reject |
| C11 | Short flags are inconsistent, no `-n` for dry run | Low | reject |
| C12 | Help examples wrap and `--managed [FALSE]` shows default `True` | Low | accept |
| C13 | `--pluginFile` is the only camelCase flag | Low | accept, plugin-file only |
| C14 | "sync" and "publish" mean different things in different commands | Low | accept |
| C15 | `--path` and `--pull [zip-or-folder]` overload one word | Medium | reject, --pull to P10 |
| P1 | `clone` is also the config editor, and doesn't create the repo | Medium | reject |
| P2 | `init` can register DEV but not PROD | Medium | reject, covered by C1 |
| P3 | `push --scope assemblyonly` is a mode, and `--no-publish` promises a command that doesn't exist | Medium | reject, wording only |
| P4 | `sync` bumps DEV before knowing whether anything changed | High | split, see text |
| P5 | `deploy` lists `dev` as a target it then rejects, and has no configure hook | Medium | split, see text |
| P6 | `provision --copy` is silently ignored for test and uat | High | accept, honour it |
| P7 | `generate --extra-tables` replaces the saved list | Low | reject, covered by C2 |
| P8 | `drift` has no `--exit-code` symmetry with `diff` | Low | closed by C9 |
| P9 | `diff` takes refs by flag only | Low | accept, positionals |
| P10 | `configure --pull` inverts the verb; planned inline edits need a grammar decision now | Medium | defer |
| P11 | `scaffold` knows one part | Low | accept |
| P12 | `status` takes no target | Low | reject |
| G1 | No `pack` | High | reject |
| G2 | No `publish` | Medium | reject |
| G3 | No `config` or `env` command | High | reject |
| G4 | No `deprovision` | Medium | defer |
| G5 | No `restore` | Medium | defer |
| G6 | No structured output anywhere | Question | defer |

All findings decided on 2026-09-08. Each `Decision:` block below is the outcome; the
proposal text above it is the original review and is kept for the reasoning.

---

## Cross-cutting

### C1. Three grammars for naming an environment

**Now.** A command addresses an environment in one of three ways, depending on which command
it is:

| Grammar | Commands | Example |
|---|---|---|
| Positional role or URL | `deploy`, `drift`, `configure` | `flowline deploy prod` |
| `--dev <url>` flag, DEV only | `push`, `sync`, `generate` | `flowline push --dev https://...` |
| `--prod/--uat/--test/--dev <url>` as config setters | `clone` (`CloneCommand.cs:30-44`), `init` (`--dev` only) | `flowline clone X --uat https://...` |

`push --dev <url>` means "push here". `clone --dev <url>` means "remember this URL as DEV, and
maybe clone from it". Same flag, two meanings. The `CloneCommand.cs:43` description says
"Development environment URL to clone solution from", the wiki says every URL is saved
regardless of source.

**Problem.** A user learns one grammar per command. An agent has to carry three rules. And
the flag grammar caps `push`, `sync` and `generate` at DEV: pushing a hotfix assembly to a
sandbox copy of TEST needs a URL and a standalone invocation.

**Proposal.** One option on every environment-touching command: `-e|--env <role|url>`.
Positional `<target>` stays on `deploy`, `drift` and `configure` because the target is the
point of those commands. `push`, `sync`, `generate` default to `dev` and accept `--env test`
or a URL. The config-setter flags on `clone` and `init` move to G3. Keep `--dev` as a hidden
alias for one release.

Decision: **accept, modified** (2026-09-08)

- Rule: required input is positional (`deploy`, `drift`, `configure` keep `<target>`),
  optional input with a default is a flag (`push`, `sync`, `generate` get `-e|--env`, default
  `dev`). Values are uniform: `prod` means the same thing in both places.
- Role keyword resolves from `.flowline`. A URL not in config: interactive run offers to save
  it under a role, non-interactive run uses it once and prints one info line naming the
  `.flowline` key to add (G3 rejected, so no `env add`). No new flag.
- Role inference, first hit wins, shared by every command that offers to save: (1) URL suffix
  `-dev`, `-test`, `-uat` before `.crm`, the same convention `provision --suffix` writes;
  (2) environment type, Production → `prod`, Developer → `dev`; (3) an unsuffixed Sandbox is
  not inferred (changed 2026-09-08 in plan review: Dataverse reports test and UAT sandboxes with
  the same type as DEV): non-interactive fails naming the `.flowline` key, interactive shows the
  picker with no pre-selection.
  Interactive: picker pre-selected with the inferred role. Non-interactive: inferred role
  used, printed as "Saved as dev (inferred from environment type). Change it in `.flowline`."
- `clone <solution> --env <url>` saves the URL through that same resolver, because clone
  creates `.flowline`; no `--role` flag. `init` always targets dev.
- `--prod/--uat/--test/--dev` retired; `--dev` hidden alias for one release.

### C2. `.flowline` is edited only through side effects of other commands

**Now.** These flags persist to `.flowline` when passed: every role URL on `clone`, `--dev`
on `init`, `--managed` on `clone` and `sync`, `--namespace`, `--extra-tables`,
`--generator`, `--output`, `--service-context-name` on `generate`, and the URL `provision`
creates. The wiki tells a user to re-run `clone` to add a UAT URL. Some descriptions say
"saved to .flowline" (`GenerateCommand.cs:33`), some don't (`SyncCommand.cs:27`). Reverting
one is `--managed false`, a grammar no other flag uses.

**Problem.** A flag that persists is a config write. A user who passes `--managed` once to
try it has changed the project for the whole team on the next commit. There is no way to see
what is set without opening the JSON, and no way to unset a URL at all.

**Proposal.** Flags are one-shot. Persistence goes through `flowline config` (G3). Until G3
ships, every persisting flag says "(saved to .flowline)" in its description, in the same
words. Drop the `--managed false` grammar in favour of `--managed` / `--no-managed`, or better,
`config set solution.managed false`.

Decision: **modify** (2026-09-08)

- Premise rejected: editing `.flowline` by hand is a supported path, so flags that persist
  are fine. Proposal's "flags are one-shot" dropped.
- Rule for every persisting flag, reusing the URL mechanism that exists today
  (`SettingsConsoleExtensions.Confirm`, `--force config`, exit 17): absent → read from
  `.flowline`; present and key empty → save silently; present and same value → nothing;
  present and different → interactive asks to overwrite, non-interactive needs
  `--force config` or exits 17.
- One help phrase for all of them: "(saved to .flowline)". `generate` has three wordings
  today; unify.
- `--managed [true|false]` stays. It is the CLI way to revert and it fits the rule above:
  saved `true`, passed `false` → overwrite prompt. Only fix: help renders
  `--managed [FALSE]  True` today, which reads as two defaults; render the placeholder as
  `[true|false]` and reword the description. No `--no-managed`.
- Consequence for G3: `config set` loses most of its case. Revisit there.

### C3. Four fixed roles is a ceiling on real team topologies

**Now.** `EnvironmentRole` is `Prod, Uat, Test, Dev` (`FlowlineCommand.cs:20`). `.flowline`
has four URL keys. The DTAP gate orders those four. `provision [role]` accepts three.

**Problem.** The model in `STRATEGY.md` is "DEV is a branch of PROD". Branches are plural. A
team running one DEV per sprint or per developer (the pattern `docs/others/todos.md` cites
from Jonas Rapp) has no way to name `dev-remy` and `dev-sprint42`. Neither does a `hotfix`
sandbox branched from PROD, a `staging`, or a `train`. Today they are all URLs, which lose the
DTAP gate, `status`, `configure`'s role-named settings file, and every keyword.

**Proposal.** Named environments: `.flowline` holds `Environments: { name: { url, role? } }`,
role is optional metadata, and the promotion chain is a list (`Chain: [dev, test, uat, prod]`
by default). Every positional `<target>` and `--env` accepts a name. `provision <name> --from
prod` replaces `provision [role]`. `SchemaVersion` already exists (`02-Project-Configuration`)
so the migration is a read-time upgrade of the four keys. This is the largest item in the
review and it changes the config schema, so the decision is cheaper before v1 than after.

Decision: **defer** (2026-09-08)

- Four roles fit the solo and small-to-medium teams the strategy targets. The C1 URL escape
  hatch covers the odd extra environment.
- If it comes back, no free-form `name` attribute. Preferred direction: numbered roles
  (`dev`, `dev1`, `dev2`, ...) so the vocabulary stays role-shaped and the DTAP chain still
  reads. Free names only if they piggyback on the PAC auth profile name, which already exists.
- Narrow option kept open: per-developer DEV URL override without a schema change.

### C4. `--skip-*` and `--no-*` both mean "turn a step off"

**Now.** `deploy` has `--skip-dtap-check`, `--skip-solution-check`, `--skip-component-check`,
`--no-backup`, `--no-delete` (`DeployCommand.cs:37-60`). `push` and `sync` have `--no-build`,
`--no-publish`, `--no-delete`.

**Problem.** Two prefixes for one idea. A user guesses `--no-dtap-check` and gets a parse
error. An agent reading `--help` has to memorise which gate uses which.

**Proposal.** Mirror `--force <specifier>`: a repeatable `--skip <gate>` with a validated
vocabulary (`dtap`, `solution-check`, `component-check`, `backup`, `build`, `publish`), and
an invalid value lists the valid ones. Keep `--no-delete` as is: it changes what the command
does, not whether a gate runs. Hidden aliases for the old flags for one release.

Decision: **reject, rule instead** (2026-09-08)

- Two prefixes stay, no renames. Repeatable `--skip <gate>` withdrawn.
- Rule, to be written into the `cli-for-agents` skill: `--skip-<check>` skips something that
  only reads and returns a verdict (dtap-check, solution-check, component-check);
  `--no-<action>` drops something that writes or has a side effect (backup, delete, build,
  publish). Testable from the noun alone. `--no-cache` is a git/npm convention and stays.
- Every existing flag already lands on the right side of that rule.

### C5. `provision --allow-overwrite` bypasses the force-specifier pattern

**Now.** `ProvisionCommand.cs:41-42`. A boolean that replaces a live environment with a copy
of PROD. `CONCEPTS.md` says bare approval by omission does not exist in Flowline, and the
`cli-for-agents` skill forbids a `bool Force`.

**Problem.** This is the most destructive write the tool can make, and it is the one hazard
that doesn't go through `--force`. `--force all` on `provision` does not cover it, which is
the opposite of what `all` promises.

**Proposal.** `--force overwrite`, added to `provision`'s valid specifiers. Remove
`--allow-overwrite`.

Decision: **accept** (2026-09-08)

- `--allow-overwrite` removed, `overwrite` added to `provision`'s specifiers, so `all` covers it.
- Behaviour follows `ConfirmGated`: existing target, interactive asks "overwrite?",
  non-interactive exits 17 naming `--force overwrite`. Replaces today's warn-and-exit.

### C6. `--dry-run` missing on five writers, and deploy's dry run writes a backup

**Now.** `--dry-run` exists on `push`, `deploy`, `configure`. Missing on `sync` (writes the
DEV version and overwrites `Solution/src`), `provision` (creates and copies an environment),
`init` (creates a publisher and a solution), `clone` (writes the repo and config), `generate`
(overwrites `Models/`). `deploy --dry-run` is described as "run every pre-flight check and
back up the target" (`DeployCommand.cs:65`).

**Problem.** The `cli-for-agents` rule is "`--dry-run` on anything that writes". `README.md`
sells "dry-run before you touch anything". A backup is a write to the target tenant: it
consumes backup quota and shows up in the admin center. A dry run that leaves a trace is not
a dry run, and an agent running `deploy --dry-run` in a loop leaves a trail of backups.

**Proposal.** `--dry-run` on `sync` (export to a temp folder, print the change summary, write
nothing, bump nothing), `provision` (resolve, validate region and type, print the plan),
`init` (validate name and prefix, report reuse or create), `generate` (list tables and output
paths). `deploy --dry-run` skips the backup; `--dry-run --with-backup` if someone wants the
old behaviour.

Decision: **reject** (2026-09-08)

- `deploy --dry-run` keeps taking the backup. A manual backup is a label on the retention
  stream, not a copy, and the create call is the only proof the caller may take one. The
  description already says so.
- No `--dry-run` on `sync` or `generate`: both are reversible with git. `sync --bump none`,
  read the summary, `git checkout -- Solution/src` is the dry run. Worth one line in wiki 07.
- `provision`, `clone`, `init`: no value. `provision` already validates region and type
  before the long copy.

### C7. Global options crowd every command's help

**Now.** `FlowlineSettings.cs:9-24` puts `-v`, `-f`, `--no-cache`, `-a` on every command.
`diff --help`, `scaffold --help` and `sln add --help` list `--no-cache` with a description
that talks about deploy, and `-a` about PAC profiles, on commands that never touch PAC or a
cache.

**Problem.** In `push --help` the first 20 lines of OPTIONS are global boilerplate before
`--scope` appears. The `cli-for-agents` rule says unused docs stay out of the agent's context.

**Proposal.** Two base settings classes: `FlowlineSettings` (`-v`, `-f`) and
`DataverseSettings` (adds `--no-cache`, `-a`, and `--env` from C1). Shorten the `--no-cache`
text to one line and move the deploy note to `deploy`'s own description.

Decision: **accept** (2026-09-08)

- `FlowlineSettings` keeps `-v`, `-f`. New `DataverseSettings` adds `--no-cache`, `-e|--env`
  (C1) and, pending C8, `-a`. `diff`, `scaffold`, `sln add` stay on the base.
- `--no-cache` description shortened to one line; the artifact-reuse note moves to `deploy`.

### C8. PAC auth profile switch prompt defaults to abort

**Now.** When the resolved profile isn't PAC's active one, interactive runs get "Switch active
PAC auth profile?" with default `false` (`ProfileResolutionService.cs:126`). Enter aborts the
command. `-a` skips the prompt and the switch is never restored.

**Problem.** Flowline already resolved the correct profile deterministically. The question has
one sensible answer, and the default is the other one. In the daily loop with a `Dev` and a
`Prod` profile this fires on every alternation between `push` and `deploy`. The long flag
name and the odd `-a` short exist only because the default is wrong.

**Proposal.** Switch without asking, print the one line Flowline already prints ("Resolved PAC
auth profile ..."), and restore the previous active profile on exit. Drop `-a`. If restore is
not wanted, keep a `--keep-profile` opt-in.

Decision: **modify** (2026-09-08)

- Model: PAC's active profile is the user's chosen working context (tenant or environment),
  global to the user, and Flowline is a guest in it. So the prompt stays, default `no` stays
  (it is a "you are about to change your global context" warning), nothing switches back,
  and `-a` stays as the flag-first consent. Proposal's default flip, restore-on-exit and
  flag removal all withdrawn.
- Resolution order changes (`DataverseConnector.cs:516-545`). Today a profile carrying the
  target URL always outranks the active UNIVERSAL one, and reachability is never checked, so
  the switch prompt fires even when the active identity could reach the target. New order:
  if the active profile can reach the target (one connect, which the command makes anyway),
  use it with no switch and no prompt; only otherwise fall through to URL matching and the
  prompt. Trade accepted: a URL-specific service-principal profile is bypassed while a user
  profile is active, which is what "my context wins" means.
- Wiki 03 "How Flowline selects a profile" updated to the new order.

### C9. Exit code 15 overloaded, drift and diff disagree, 130 for a declined prompt

**Now.** `ValidationFailed` (15) covers: drift found by `drift` (`DriftCommand.cs:200`), drift
found by `deploy`, an invalid `--force` value, contradicting flags, missing dependencies, an
unsupported `init` environment type. `diff` reports changes as 22 only with `--exit-code`.
Declining the first-import confirmation returns 130 `Cancelled` (`DeployCommand.cs:214`), the
Ctrl+C code. 14 `VersionConflict` is published in wiki 11 with a corrective action and has no
throw site (`ExitCode.cs:41-46`).

**Problem.** An agent gating on `drift` can't tell "environment has drift" from "you passed a
bad flag". A user runs `drift` to get a report and gets a failure code for a successful
report. Two comparison commands, same outcome, different codes. A deliberate "no" is not a
SIGINT.

**Proposal.** `drift` follows `diff`: exit 0 on a report, 22 `ChangesFound` with
`--exit-code`. Split 15 into `ValidationFailed` (inputs) and a new `DriftDetected` if deploy's
drift gate needs its own code. A declined confirmation returns 17 `ForceRequired` with the
specifier named, since that is the fix. Mark 14 as reserved in wiki 11 or remove the row.

Decision: **accept, narrowed** (2026-09-08)

- `drift` follows `diff`: exit 0 on a report, 22 `ChangesFound` only with `--exit-code`.
  Wiki 04 drift exit-code paragraph updated. Closes P8.
- Declined first-import prompt exits 17 `ForceRequired` naming `--force first-import`, same
  as the non-interactive path already does. 130 stays for Ctrl+C only.
- 14 `VersionConflict` marked reserved in wiki 11 (no throw site).
- 15 left as is. No caller needs to tell deploy's drift gate from bad input yet.

### C10. `--client-secret` on the command line

**Now.** `GenerateCommand.cs:60-61`. The wiki shows it in a CI example. `AZURE_CLIENT_SECRET`
is also read.

**Problem.** A secret in argv lands in shell history, process listings, and Flowline's own
invocation log unless the scrubber catches it. The env var path already exists and is the
one CI should use.

**Proposal.** Remove the flag. Read `AZURE_CLIENT_SECRET`, or prompt interactively. If a flag
is wanted, `--client-secret-env <NAME>` or `--client-secret-stdin`.

Decision: **reject** (2026-09-08). Flag stays. PAC CLI takes `--clientSecret` in argv itself,
and the flag only exists for the two non-default XrmContext generators.

### C11. Short flags are inconsistent, no `-n` for dry run

**Now.** `push` has `-s`, `-p`, `-w`. `generate` and `scaffold` have `-o`. Nothing else has
short flags except the globals. `--dry-run` has none, and `-a` is spent on the auth switch.

**Proposal.** `-n|--dry-run` on every command that has it (git, rsync, make convention). Drop
`-a` (C8). Drop `-p`, `-w`, `-s` or keep them; either way, don't add new short flags below the
fold.

Why `-n`: it is the established short form for a dry run in `make -n`, `rsync -n`, `git
clean -n`, `git add -n`, `git rm -n`. The letter comes from make's original `-n`, "no act":
print the commands, execute nothing. Users who know any of those tools reach for it without
reading help.

Decision: **reject** (2026-09-08). `--dry-run` is typed rarely enough that the long form is
fine. Existing short flags stay as they are.

### C12. Help examples wrap and `--managed [FALSE]` shows default `True`

**Now.** Top-level `--help` shows five examples, three of them long URLs that wrap mid-flag on
an 80-column terminal. Two `WithExample` calls pack several args into one string
(`Program.cs:178,179`), which the `cli-for-agents` skill already flags. `sync --help` renders
`--managed [FALSE]` with a DEFAULT column reading `True`.

**Proposal.** Top-level examples: `clone`, `push`, `sync`, `deploy prod`, all short. Per-command
examples keep the long forms. `--managed` grammar goes with C2.

Decision: **accept** (2026-09-08). Verified against Spectre.Console.Cli 0.55.0:

- Root help has no examples registered, so Spectre fills it from the children's examples,
  capped by `MaximumIndirectExamples` (default 5). `config.AddExample(...)` at root level
  replaces that with `clone ContosoSales`, `push`, `sync`, `deploy prod`.
- Wrapping is the terminal's, not Spectre's. Short root examples are the only fix; long URL
  forms stay on the per-command pages.
- `--managed [FALSE]` is the option template's placeholder, uppercased. Change the template to
  `--managed [true|false]` (`|` already works in `--copy <minimal|full>`). The DEFAULT column
  then reads correctly and stays. `ShowOptionDefaultValues` or a `GetOptions` override in
  `FlowlineHelpProvider` exist if it ever needs hiding.
- The two packed `WithExample` strings (`Program.cs:178,179`) split into one arg per string.

### C13. `--pluginFile` is the only camelCase flag

**Now.** `PushCommand.cs:44`. Every other flag is kebab-case.

**Proposal.** `--plugin-file`, with `--pluginFile` as a hidden alias for one release. Same
review for `--webresources` vs `--web-resources`; pick one form and apply it to `--scope`
values too.

Decision: **accept, `--plugin-file` only** (2026-09-08)

- Origin was `pac plugin add --pluginFile`. PAC has no single convention (`--applicationId`
  vs `--publisher-prefix`), and Flowline already chose kebab elsewhere, so the inheritance
  isn't visible to a `flowline push` user.
- `--plugin-file`, with `--pluginFile` kept as a hidden alias.
- `--webresources` stays: it is already the folder name, the `--scope` value and the
  `scaffold` part, so it is one consistent token.

### C14. "sync" and "publish" mean different things in different commands

**Now.** `push --no-publish` reads "Skip publishing web resources and form event handlers
after sync" (`PushCommand.cs:67`). "sync" there is the push's reconciliation, not the `sync`
command. The wiki says `--no-publish` is for "pushing several solutions and publishing once at
the end", and there is no command that publishes (G2).

**Proposal.** "after the push". Every description that uses "sync" as a verb for anything but
the `sync` command gets reworded.

Decision: **accept** (2026-09-08). Wording only. "sync" is reserved for the `sync` command,
"publish" for the Dataverse publish operation. Sweep `[Description]` attributes, console
messages and wiki 04/05/06 for both. G2 decides separately whether a `publish` command exists.

### C15. `--path` and `--pull [zip-or-folder]` overload one word

**Now.** `deploy --path <zip>` and `drift --path <zip>` take an artifact. `configure --pull
[zip-or-folder]` is a mode switch that optionally takes a source. `scaffold --output <PATH>`
and `generate -o|--output <PATH>` take a folder. `--path` also silently selects standalone
mode when no project is found.

**Proposal.** `--artifact <zip>` on `deploy` and `drift`. `configure --pull` stays a bare
switch; the source becomes `--from <zip|folder>` (see P10). Standalone mode stays implicit
but the help text for `--artifact` says so.

Decision: **reject for `--path`, `--pull` carried to P10** (2026-09-08)

- `--path` stays. It is the flag of `pac solution import --path`, which `deploy` wraps. PAC
  uses `--zipfile` only on pack/unpack. Help text says "the packed solution zip" so the
  description carries what the name doesn't. `--artifact` withdrawn.
- `configure --pull [zip-or-folder]` is decided with the `configure` grammar in P10.

---

## Per command

### P1. `clone` is also the config editor, and doesn't create the repo

**Now.** `clone` saves every role URL it receives (`CloneCommand.cs:58-62`), picks the source
by scanning prod, uat, test, dev in that order, and the wiki says to re-run `clone` with one
flag to register a new environment. `clone` writes into the current folder; the user runs
`mkdir`, `cd`, `git init` first (wiki 01).

**Problem.** The name says `git clone`. `git clone` creates the folder and the repo. A user
who types `flowline clone ContosoSales` in `~/src` gets a project scaffolded into `~/src`.
Re-running a bootstrap command to edit config is the wrong tool for the job.

**Proposal.** `clone <solution> [dir]`: default `dir` = solution name, create it, `git init`
when not inside a repo, scaffold there. `--env <role|url>` (C1) names the source. Config
registration moves to G3. `--managed` stays as the one solution-level option.

Decision: **reject** (2026-09-08)

- Folder and repo: git is the user's job, Flowline wraps no git commands. `clone` writes
  into the folder you chose, same as `dotnet new`, `pac solution init`, `npm init`; the
  `git clone` analogy was the reviewer's, not the tool's. Clone's preflight already fails
  outside a git repo before writing anything.
- Source selection: `--env` names the source. Absent: one configured URL → use it; more
  than one → interactive asks which, non-interactive fails naming `--env`. No default, no
  scan. The prod → uat → test → dev order goes away with the role flags.
- Config registration: settled by C1.

### P2. `init` can register DEV but not PROD

**Now.** `init` takes `--dev` only (`InitCommand.cs:41`). A greenfield project's PROD URL gets
registered later, by running `clone --prod <url>` on a project that is already cloned.

**Proposal.** Falls out of G3. Until then, `init` accepts the same URL flags as `clone`, or
neither does.

Decision: **reject, covered by C1** (2026-09-08). `init` stays dev-only. The first
`deploy <prod-url>` after init offers to save the URL as `prod` (type Production inferred),
or the user adds the line to `.flowline` by hand.

### P3. `push --scope assemblyonly` is a mode, and `--no-publish` promises a command that doesn't exist

**Now.** `--scope` values: `all, webresources, plugins, assemblyonly` (`PushCommand.cs:40-41`).
`assemblyonly` is "plugins, but skip step and Custom API registration". `[solution]` in
project mode does nothing but validate against config.

**Proposal.** `--scope plugins|webresources` plus `--assembly-only` (or `--skip registration`
under C4). The positional `[solution]` stays for standalone; in project mode the mismatch
error is fine. `--no-publish` needs G2 to make sense; otherwise reword it as "leave the
environment unpublished".

Decision: **reject, wording only** (2026-09-08)

- `--scope` stays as is. Scope is "what gets pushed", and `assemblyonly` is a subset of
  `plugins`, which is fine. Optional help nit: add "(assemblyonly: the plugin assembly bytes
  without step or Custom API registration)".
- `--no-publish` stays. The wiki 04 sentence "publishing once at the end" reads as if
  Flowline does that publish; reword to name the last `push` without the flag,
  `pac solution publish`, or the portal. Description reword is C14. `flowline publish` is G2.
- `[solution]` positional stays.

### P4. `sync` bumps DEV before knowing whether anything changed

**Now.** The version bump runs against DEV before the export (`SyncCommand.cs:99-119`), so
every `sync` writes to DEV and produces at least a `Solution.xml` version diff. Default bump
is `patch` (`SyncCommand.cs:33`). `BumpVersion` increments index 2 of the four-part Dataverse
version (`SyncCommand.cs:186`), which Microsoft calls build, while the flag calls it patch and
the command description says "bump build version". `sync` also runs `dotnet build` unless
`--no-build`. `pull` is an alias.

**Problem.** An agent that retries `sync` three times ships three version numbers for one
change. A CI job that runs `sync` on a schedule to detect maker-portal changes gets a commit
every run even when nothing changed. A "pull" that mutates the remote is a surprise to anyone
arriving from git. The `cli-for-agents` skill documents the unconditional bump as
deliberate; this review disagrees for the no-change case only.

**Proposal.** Export first, compare, bump only when the change summary is non-empty, then
re-export or patch the version into the unpacked XML and DEV together. Rename values to
Dataverse terms (`major|minor|build|revision`) with `patch` kept as an alias of `build`.
Consider `pull` as the primary name with `sync` as alias, matching `push`. Make the build step
opt-in (`--build`) or explain in the description why a pull compiles.

Decision: **split** (2026-09-08)

- P4a bump-before-export: **reject**. Every export from the maker portal bumps the version
  too (wiki 12 §3); the no-change case doesn't justify a different default. `--bump none`
  covers it.
- P4b semver names: **reject**. `patch|minor|major` is the industry standard and stays. One
  consistency fix: the command description says "bump build version" (`Program.cs:189`);
  change to "patch".
- P4c `dotnet build` in sync: **accept, wording only**. Keep the step, say in the description
  why a pull compiles.
- P4d `pull` primary, `sync` alias: **accept, full sweep**. Reasoning: the loop
  `clone → push → sync → deploy` is a git sentence with one PAC word in it, `configure
  --pull` already uses the word, and every decision in this review treated git as the
  reference model. `sync` stays a permanent alias so nothing breaks. Sweep: README (10),
  wiki (121, retitle 07), descriptions and scaffolded templates (33), plugin skills (25),
  CHANGELOG entry. Description must carry what "pull" undersells: version bump, build,
  `CHANGES.md`, context docs.

### P5. `deploy` lists `dev` as a target it then rejects, and has no configure hook

**Now.** `<target>` description says "prod, uat, test, dev, or a URL" (`DeployCommand.cs:30`);
the DTAP gate's `DevBlock` rejects `dev` (`DeployCommand.cs:468`) unless `--skip-dtap-check`.
`deploy` has no `--settings-file`; `configure` is a separate command and `STRATEGY.md` defers
"auto-applying configuration after a deploy".

**Problem.** Help says one thing, runtime says another. Users of Power Platform Build Tools
expect the import step to take the settings file; running two commands per environment in a
pipeline is a step they have to discover.

**Proposal.** Drop `dev` from the description, or say "dev only with --skip-dtap-check".
Decide the hook now so the surface is stable: `deploy test --configure` applies
`deploymentSettings.test.json` after import, or auto-apply when the role-named file exists
and `--no-configure` opts out. Pick one; the second matches "configuration flows down".

Decision: **split** (2026-09-08)

- P5a: **accept**. Drop `dev` from the `<target>` description. Gate behaviour unchanged.
- P5b: **defer**. `deploy --settings-file <path>` is the planned shape, added once
  `configure` has been tested enough in the field. Same flag name as `configure`, so the two
  share the file. Not auto-apply.

### P6. `provision --copy` is silently ignored for test and uat

**Now.** `ProvisionCommand.cs:158`: `test` and `uat` always copy full, whatever `--copy`
says. The description on `:34` says so in a parenthesis.

**Problem.** A flag that is accepted and ignored is a lie to the caller. An agent passing
`--copy minimal` to provision a cheap UAT gets a full copy and a bill for storage.

**Proposal.** Honour it, or reject it with `ValidationFailed` naming the rule. Honouring it is
simpler and removes a rule the user has to know.

Decision: **accept, honour it** (2026-09-08). `--copy` applies to every role. Defaults stay:
minimal for dev, full for test and uat. Description and wiki 04 updated to say so.

### P7. `generate --extra-tables` replaces the saved list

**Now.** `GenerateCommand.cs:41`. Passing `--extra-tables contact` after `account,contact`
was saved drops `account`.

**Proposal.** Falls out of C2 and G3 (`config set generate.extraTables ...`). Until then, say
"replaces" in bold or add `--add-table`.

Decision: **reject, covered by C2** (2026-09-08). A different value than the saved list
triggers the overwrite prompt (interactive) or requires `--force config` (non-interactive),
which is what prevents the accidental drop. No `--add-table`.

### P8. `drift` has no `--exit-code` symmetry with `diff`

Covered by C9. Listed here so the per-command table is complete.

Decision: **closed by C9** (2026-09-08). `drift` gets `--exit-code`, exit 22 on changes.

### P9. `diff` takes refs by flag only

**Now.** `--from <REF> --to <REF>` (`DiffCommand.cs:34-39`).

**Proposal.** Also accept positionals like git: `diff v1.2.0 v1.3.0`, `diff v1.2.0`. Flags stay.
Low priority, but it is the one command whose grammar every user already knows from git.

Decision: **accept, positionals replace the flags** (2026-09-08)

- `--from`/`--to` removed; `diff` shipped 2026-09-06, nothing to keep compatible.
- Grammar: `diff` (HEAD vs working tree), `diff A` (A vs working tree), `diff A B`,
  `diff A..B` (same as `A B`), `diff A...B` (merge-base of A and B, vs B; resolved with
  `git merge-base`). The three-dot form is the branch-review question the diff plan lists as
  a future extension.
- Nothing else from git (`--cached`, `--staged`, path filters): `diff` reasons in components.
- `--write` and `--exit-code` stay.

### P10. `configure --pull` inverts the verb; planned inline edits need a grammar decision now

**Now.** `configure <target>` applies a file; `--pull` makes the same command capture one
(`ConfigureCommand.cs:50`). Wiki 13 plans `configure prod flow "Approval Flow" --on`, which
adds a positional component type and name after `<target>`. The `todos.md` notes explore
`-c/--category`, `--name`, `--id`, `--enable/--disable`, `--list`.

**Problem.** One command is growing three modes (apply, capture, edit one) and a filter
language. Flags on a single verb won't carry that without contradicting-flag validation for
every pair.

**Proposal.** Decide the shape before the inline work: subcommands `configure apply <target>`,
`configure pull <target>`, `configure set <target> <kind> <name> --on|--off|--value`,
`configure list <target> [kind]`. Bare `configure <target>` stays as an alias of `apply` for
the pipeline case. `--settings-file` keeps its name so `deploy --configure` (P5) can share it.

Decision: **defer** (2026-09-08). To be decided with the inline-edit and secret-resolution
work. Shapes on the table: subcommands (`apply|pull|set|list`, recommended, absorbs the
`--pull [zip-or-folder]` overload from C15 as `pull --from`) or mode flags on one verb.
`--pull` stays as is until then.

### P11. `scaffold` knows one part

**Now.** `<part>` is `webresources` only (`ScaffoldCommand.cs:37`). A second plugin project is
a supported layout (wiki 02), yet there is no `scaffold plugins`.

**Proposal.** Add `plugins` with `--name`. The `new` alias is fine.

Decision: **accept** (2026-09-08). `scaffold plugins [--name]` using the template `clone` and
`init` already write; same solution-file lookup and skip-if-present rule as `webresources`.

### P12. `status` takes no target

**Now.** Always the whole grid. Fine for a project. Low value: `status prod` to check one
environment's auth before a deploy. Skip unless C3 lands, where a named-environment list
makes a filter useful.

Decision: **reject** (2026-09-08). Four roles at most, the grid is cheap, a filter buys little.

---

## Gaps

### G1. No `pack`

**Now.** The only way to produce the artifact is `deploy`, which caches it in `artifacts/`
and reuses it on the next unchanged deploy. A CI pipeline that wants "build once in job A,
`deploy --path` in jobs B, C, D" has to run a deploy (or a `deploy --dry-run`, which today
also takes a backup, C6) to get the zip.

**Proposal.** `flowline pack [--output <zip>]`: pack from `Solution/src`, run the git-clean
and drift checks, write the artifact, print its path, exit 0. `deploy` keeps its cache.

Decision: **reject** (2026-09-08). `pac solution pack --zipfile <zip> --folder Solution/src
--packagetype Unmanaged` is the whole job and `deploy --path` reads name and managed flag
from the zip. The drift check needs a DEV connection, so a wrapper can't add it. Philosophy
§7: PAC has this one. Document the `pac solution pack` line in wiki 08 "Artifacts" as the
build-once step next to `deploy --path`.

### G2. No `publish`

**Now.** `push --no-publish` exists so a user can publish once after several pushes. Nothing
in Flowline publishes on its own.

**Proposal.** `flowline publish [--env]` wrapping `PublishAllXml`, or `push --publish-only`.
The first is one line to describe and matches the flag it completes.

Decision: **reject** (2026-09-08). `pac solution publish` exists, and the last `push` without
`--no-publish` publishes. Wording fix is P3b.

### G3. No `config` or `env` command

**Now.** See C2. Reading `.flowline` means opening it; writing it means running a command
whose main job is something else.

**Proposal.** `flowline config list`, `config set <key> <value>`, `config unset <key>`.
With C3, `flowline env list|add <name> <url> [--role]|remove <name>`. `status` already
renders most of `config list` for environments, so `env list` can reuse it.

Decision: **reject** (2026-09-08). `config set` lost its case in C2 (flags persist
themselves, `.flowline` is hand-editable). `env add` not added either: the C1 interactive
prompt covers humans, and the non-interactive hint names the key instead of a command:
"Not saved. Add `"DevUrl": "https://..."` to `.flowline` to keep it." Validation happens
on the next command that reads the URL. C1's decision text updated to match.

### G4. No `deprovision`

**Now.** `provision` creates environments and records URLs; nothing removes either. Already in
`docs/others/todos.md` with guards listed there.

**Proposal.** `flowline deprovision <name>` (or `env delete`), refuses Production type and the
configured PROD URL, `--force delete-environment`, removes the URL from `.flowline`. With C3
this is the natural end of a per-sprint DEV.

Decision: **defer** (2026-09-08). Liked. Unlike G1/G2 the wrapper adds what PAC lacks: the
guards (refuse Production type and the PROD URL outright, `--force delete-environment`,
remove the URL from `.flowline`). Shape: `deprovision <dev|test|uat>`. Decide later.

### G5. No `restore`

**Now.** `deploy` takes a labelled backup before every import. There is no command to list
those backups or restore one. `docs/ALM-strategy.md` §9 names environment restore as the only
rollback in the unmanaged model.

**Proposal.** `flowline restore <target> [--backup <label>]`, listing Flowline-labelled backups
when none is named, `--force restore-environment`. Half the ripcord already exists.

Decision: **defer** (2026-09-08), with G4. `pac admin restore` is the primitive; Flowline
would add knowing its own labels and refusing a restore into PROD without the force
specifier. Decide later.

### G6. No structured output anywhere

**Now.** The `cli-for-agents` skill rule 8: no `--json`, exit code plus text lines is the
contract.

**Question, not a finding.** That holds for the writers. `status`, `diff`, `drift` and
`configure --dry-run` are reports that agents and pipelines read back. A PR bot posting the
`diff` summary, or a dashboard reading `status`, parses glyphs and columns today. The rule was
written for the write path; decide whether the four report commands are exempt.

Decision: **defer** (2026-09-08). Rule 8 stands. Revisit when the first real consumer
exists (the PR bot posting the `diff` summary, STRATEGY "Distribution"); adding `--json`
before that guesses the schema. If it comes, it is `status`, `diff`, `drift`,
`configure --dry-run` only, writers stay text.

---

## Naming consistency table

Every option name this review touched, with the decided outcome.

| Now | Decided | Finding |
|---|---|---|
| `--dev <url>` (push, sync, generate) | `-e\|--env <role\|url>`, default `dev` | C1 |
| `--prod/--uat/--test/--dev <url>` (clone, init) | retired; `clone --env <url>` saves via role inference, `--dev` hidden alias one release | C1 |
| `--managed [false]` | stays; placeholder rendered `[true\|false]`, help reworded | C2, C12 |
| `--skip-dtap-check` etc. | stay; rule: `--skip-<check>` reads, `--no-<action>` writes | C4 |
| `--allow-overwrite` | `--force overwrite` | C5 |
| `--dry-run` | stays, no short flag | C11 |
| `-a\|--auto-select-auth-profile` | stays; resolver prefers the active profile when it reaches the target | C8 |
| `--client-secret <SECRET>` | stays | C10 |
| `--pluginFile` | `--plugin-file`, old spelling hidden alias | C13 |
| `--path <zip>` (deploy, drift) | stays, help says "the packed solution zip" | C15 |
| `--pull [zip-or-folder]` | stays until P10 | C15, P10 |
| `--scope assemblyonly` | stays, help notes it is a subset of `plugins` | P3 |
| `--bump patch` | stays; command description says "patch" not "build" | P4 |
| `sync` | `pull` primary, `sync` permanent alias, full doc sweep | P4 |
| `deploy <target>` help | `dev` dropped from the list | P5 |
| `--copy` on test/uat | honoured | P6 |
| `diff --from/--to` | positionals `[from] [to]`, `A..B`, `A...B` | P9 |
| `drift` exit on findings | 0; 22 with `--exit-code` | C9 |
| declined first-import prompt | 17, was 130 | C9 |
| `scaffold <part>` | adds `plugins` | P11 |

## Target surface after the decisions

Only what changes. Everything not listed stays as it is today.

```
flowline clone <solution> [--env <role|url>] [--managed [true|false]]
flowline push  [--env <role|url>] [--plugin-file <path>] ...
flowline pull  [--env <role|url>] [--managed [true|false]] [--bump ...] [--no-build]   (sync as alias)
flowline generate [--env <role|url>] ...
flowline deploy <prod|uat|test|url> ...                                                (no dev in help)
flowline drift  <target> [--path <zip>] [--exit-code]
flowline diff   [<from> [<to>]] [--write [file]] [--exit-code]                        (A..B, A...B)
flowline provision [dev|test|uat] [--copy minimal|full] [--force overwrite]
flowline scaffold webresources|plugins [--name] [-o]
```

Global: `-h`, `-v|--verbose`, `-f|--force <specifier>`. Dataverse-touching commands add
`--no-cache`, `-a`, `-e|--env`.

Deferred, shape not fixed: C3 numbered roles, P5b `deploy --settings-file`, P10 `configure`
subcommands, G4 `deprovision`, G5 `restore`, G6 `--json` on report commands.
