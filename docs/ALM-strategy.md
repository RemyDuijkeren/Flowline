# Dataverse ALM — Steps and Strategies

Reference for how Dataverse solution ALM works: Microsoft's documented guidance, the strategies
teams actually run, and where Flowline's default model diverges. Background for scoping decisions —
not a Flowline how-to. For the product workflow see [`README.md`](../README.md); for the direction
see [`STRATEGY.md`](../STRATEGY.md).

Every platform claim below links to its source. Microsoft guidance and community practice are
attributed separately, because on the central question — where the source of truth lives — they
disagree.

---

## 1. The layer model

Everything downstream follows from this, so it comes first.

Dataverse evaluates two layer levels
([Solution layers](https://learn.microsoft.com/power-platform/alm/solution-layers-alm)):

- **Managed layers** — the system solution at the base, then each installed managed solution.
  Last installed sits on top and can customize the ones below. Uninstall one and the layer beneath
  takes effect.
- **The unmanaged layer** — a single shared layer above all managed layers. Every unmanaged solution
  and every ad-hoc customization writes into it.

Layering is per component, not per solution. A component has an unmanaged layer only if someone
customized that component.

Two consequences drive most ALM design:

**The unmanaged layer always wins.** An unmanaged customization on a component means later managed
updates to that component stop taking effect — import reports success, behavior doesn't change. The
troubleshooting article walks the layer tables: with active value `C` on top, upgrading the managed
solution from `B` to `D` leaves `C` effective
([Changes aren't effective after solution import](https://learn.microsoft.com/troubleshoot/power-platform/dataverse/working-with-solutions/changes-not-effective-solution-import),
[Solution layers](https://learn.microsoft.com/power-platform/alm/solution-layers-alm)).
An unmanaged layer also blocks uninstalling the managed solution that owns the component.

Whether that precedence is a defect or a feature depends on who you are: it is the environment
owner's final say over everything shipped to them, and a trap for whoever ships the update. §10.4
reads the whole managed model through that lens.

**Unmanaged solutions create no layers.** They are containers; on import their contents merge into
the one unmanaged layer, overwriting whatever definition was there
([Best practices for ALM in Dynamics 365 applications](https://learn.microsoft.com/dynamics365/guidance/implementation-guide/application-lifecycle-management-product)).
So "unmanaged deploy" is last-write-wins in a shared bucket, not a precedence relationship.

Reset for a stuck component: solution → component → Advanced → **See solution layers** → select the
unmanaged layer → **Remove unmanaged layer**.

---

## 2. Environment topology

Microsoft's minimum is dev + prod; the recommendation is dev + test + prod, plus UAT/SIT/pre-prod as
needed ([Environment strategy for ALM](https://learn.microsoft.com/power-platform/alm/environment-strategy-alm)).

- Prod is a **Production** type environment; every other environment is a **Sandbox**
  ([ALM strategy guidance](https://learn.microsoft.com/microsoft-copilot-studio/guidance/alm)).
- Access is split by purpose: makers and developers in dev, testers in test, users in prod. Makers
  and developers get no prod access, or user-level only
  ([ALM basics](https://learn.microsoft.com/power-platform/alm/basics-alm#environments)).
- **Service update stations.** A solution imports into an environment on the same or a newer
  Dataverse version, not reliably into an older one. Dev environments must therefore sit in a
  station that updates at the same time as or earlier than prod — US prod with Canadian dev breaks
  imports
  ([Multi-geographical considerations](https://learn.microsoft.com/power-platform/alm/environment-strategy-alm#multi-geographical-considerations)).

Dev granularity is a separate axis: one shared dev environment is simplest and lets developers
overwrite each other; one environment per developer or per branch isolates work and requires
provisioning automation.

Environment-type facts that constrain automation:

- `pac solution clone` and `pac solution sync` work against a **Developer** environment.
- `pac admin copy` cannot target a Developer environment, so an environment branched from prod by
  copy is always a **Sandbox**.

---

## 3. Solution strategy

Two documented shapes ([Organize your solutions](https://learn.microsoft.com/power-platform/alm/organize-solutions)):

| | Single solution | Multiple + dedicated dev environments |
|---|---|---|
| Fit | small/medium, modularization unlikely | enterprise, multiple teams or partners |
| Deploy | one artifact | independent module deploys, faster CI |
| Cost | large solution imports slowly | environment sprawl, layering discipline |

Multi-solution layering: build a base unmanaged solution in a base dev environment, export it
**managed**, import it into each app dev environment, and layer app solutions unmanaged on top. Use
the **same publisher and prefix** for every solution across every environment.

Large single solutions: use
[table segmentation](https://learn.microsoft.com/power-platform/alm/segmented-solutions-alm) to ship
only the columns you touched rather than whole tables.

**The golden rules**, stated as such by Microsoft
([ALM strategy guidance](https://learn.microsoft.com/microsoft-copilot-studio/guidance/alm)):

1. Customize only in a development environment.
2. Always work in the context of solutions.
3. Use a custom publisher and prefix.
4. Create separate solutions only to deploy components independently.
5. Use environment variables for settings and secrets that change across environments.
6. Export and deploy managed, except when setting up a development environment.
7. Automate source control and deployment.

Rule 6 is the one Flowline's default model inverts — see §9.

---

## 4. The development loop

Ordering matters because plugins and web resources are Dataverse-resident solution components. They
must exist in the dev environment before export, or they are absent from the artifact.

**Inner loop, in DEV:**

1. Pack and import the unmanaged solution from source — only when seeding a fresh or empty dev
   environment. An existing dev environment already holds the state.
2. Make declarative changes in the maker portal: tables, columns, forms, views, flows.
3. Build code locally: plugin/workflow assembly, web asset bundle. Unit tests run here without
   Dataverse.
4. Push compiled artifacts into DEV:
   - Plugin assembly, or a **plugin package** when there are dependent assemblies
     ([Build and package plug-in code](https://learn.microsoft.com/power-apps/developer/data-platform/build-and-package)).
   - Register or update **steps** — message, table, stage, execution mode, filtering attributes,
     execution order
     ([Register a plug-in](https://learn.microsoft.com/power-apps/developer/data-platform/register-plug-in)).
   - Upload web resource files, then publish customizations.
5. **Add each piece to the solution separately.** The Plug-in Registration Tool registers into the
   system **Default** solution, not yours. The assembly must be added to your unmanaged solution,
   and *each registered step must be added individually* — steps do not travel with the assembly
   ([Assembly registration](https://learn.microsoft.com/power-apps/developer/data-platform/register-plug-in#assembly-registration)).
   A step left out of the solution is the classic "works in dev, silent in prod" defect.
6. Smoke test in DEV.

**Sync to source control:**

7. Export the **unmanaged** solution.
8. Unpack it into the repo. Manual editing of unpacked component files is unsupported except for
   specific sections of `customizations.xml`
   ([Source control with solution files](https://learn.microsoft.com/power-platform/alm/use-source-control-solution-files)).
9. Pull, reconcile conflicts, commit, open a PR, review the diff.

### Unmanaged only, or both?

Microsoft's current position: "Git should only include your source code and unmanaged customizations.
Managed versus unmanaged is determined when building and releasing the solution"
([Git integration FAQs](https://learn.microsoft.com/power-platform/alm/git-integration/faqs)). Storing
both representations was common before Git integration and is presented as no longer needed.

The constraint that decides it for you: **SolutionPackager cannot convert one type to the other. The
only way to get a managed solution from an unmanaged one is to import the unmanaged zip into a
Dataverse environment and export it as managed**
([SolutionPackager tool](https://learn.microsoft.com/power-platform/alm/solution-packager-tool#managed-and-unmanaged-solutions)).

So the choice follows from how the release produces its managed artifact:

- **Unmanaged source only** — correct when the managed zip comes from exporting managed out of a dev
  or build environment, and correct always in the model of §9, where nothing managed is ever shipped.
- **`--packagetype Both`** — needed only to pack a managed zip straight from source with no
  environment round-trip. It requires exporting the solution *twice* (`AnyName.zip` and
  `AnyName_managed.zip` side by side); unpacking both into one folder preserves the managed/unmanaged
  differences, and either type can then be packed from that folder.

Defaults differ per verb, which is easy to miss: `pac solution unpack` and `pack` default to
`Unmanaged`, while **`pac solution sync` defaults to `Both`**
([pac solution](https://learn.microsoft.com/power-platform/developer/cli/reference/solution#pac-solution-sync)).

**CI, on merge:**

10. Restore, build code projects, run unit tests.
11. Pack the solution from source (`pac solution pack`, or the `cdsproj` build, which embeds built
    assemblies and web resources) into one versioned artifact.
12. Run **Solution checker** as a quality gate
    ([Solution checker](https://learn.microsoft.com/power-platform/admin/managed-environment-solution-checker)).
13. Publish the artifact.

**CD, per target:**

14. Import managed into TEST with that environment's **deployment settings file**.
15. Run automated and acceptance tests.
16. Approval gate, then import **the same artifact** to PROD. Rebuilding between test and prod
    discards the evidence the tests produced.

### When to run solution checker

Solution checker performs static analysis of solution components against a rule set, returning
findings by severity with links to the rule documentation
([Solution checker rules](https://learn.microsoft.com/power-apps/maker/data-platform/use-powerapps-checker#best-practice-rules-used-by-solution-checker)).
It runs in three places, and they answer different questions.

| Placement | Mechanism | Nature |
|---|---|---|
| **Maker portal, on demand** | Solutions → ⋯ → Solution checker → Run / View results / Download results | Ad-hoc inspection while building |
| **Build or pre-import** | `pac solution check --path <zip>`, the Azure DevOps Checker task, or the GitHub action | Feedback to the author, and the gate most pipelines enforce |
| **Import into a Managed Environment** | Solution checker enforcement: **None**, **Warn**, or **Block** ([enforcement settings](https://learn.microsoft.com/power-platform/admin/managed-environment-solution-checker)) | Platform-side gate, owned by admins, independent of your pipeline |

The build placement is the common one, run as a gate before promotion. Enforcement at import is the
enterprise backstop: with **Warn** the import proceeds and a summary email is sent, with **Block**
the import is cancelled before any change reaches the environment. **Only critical severity rules
block** — a gate keyed on critical findings matches platform behavior; keying on total findings does
not.

Two constraints that decide how a pipeline should invoke it:

- **Enforcement only honors clean runs.** To have results count toward Managed Environment
  enforcement, use the Solution Checker ruleset (`pac solution check` uses it by default) and pass
  **no file exclusions and no rule overrides** — Microsoft states these are not supported for
  enforcement
  ([troubleshooting guide](https://learn.microsoft.com/troubleshoot/power-platform/dataverse/working-with-solutions/solution-checker-enforcement-import-issues)).
  A pipeline that adds exclusions still produces a useful report, but its results stop satisfying
  the platform gate, and imports begin failing server-side after passing locally.
- **Results are cached per tenant.** `pac solution check --clearCache` clears the enforcement cache
  of past results for your solutions, which matters when re-running against a solution the platform
  has already judged.

Optional: `--saveResults` stores the analysis in the environment so it appears in the Solution
Health Hub app, which gives admins visibility into what the pipeline saw.

Placement is a latency trade. Running the checker on every capture-to-source step gives the earliest
feedback, but it is a service upload and analysis measured in minutes, and putting it on the most
frequently run command reliably leads to that command being avoided or bypassed. Gating at promotion
and offering the check on demand earlier is the better default.

---

## 5. Versioning

Format is `major.minor.build.revision`, and an update must be strictly higher than the parent on
some segment
([Understanding version numbers for updates](https://learn.microsoft.com/power-apps/maker/data-platform/update-solutions)).

### Where the bump happens

The number exists in two places — the solution record in Dataverse, and `Solution.xml` in the
unpacked source — so there are three points in the loop (§4) where it can be incremented, each with
its own tooling.

| # | Bump point | Tooling | Effect |
|---|---|---|---|
| 1 | **In the dev environment, by hand, before export** | Maker portal | Microsoft's documented manual step: "Increment the version number when you export the solution." |
| 2 | **In the dev environment, by automation, before export** | `pac solution online-version --solution-name X --solution-version 1.0.0.2`; ADO task `PowerPlatformSetSolutionVersion@2` | Dataverse and the exported artifact agree; the number reaches source control as part of the export. |
| 3 | **On the unpacked source, at build/pack time** | `pac solution version --buildversion / --revisionversion / --strategy` (`GitTags`, `FileTracking`, `Solution`) | The artifact is versioned; the dev environment still shows the old number. |

There is no fourth point — import has no version parameter, and the number that arrives is whatever
the artifact carries.

### What's most common

**Point 3, driven by the CI run number, is the mainstream pro-dev practice**, and Microsoft states
the convention directly. The Build Tools guidance: "While version number can be hardcoded in the
pipeline, it is recommended to use an Azure DevOps pipeline variable like BuildId"
([Build Tools tasks](https://learn.microsoft.com/power-platform/alm/devops-build-tool-tasks#solution-tasks)).
The code-components ALM page gives the mapping explicitly — `MAJOR`/`MINOR` from pipeline variables
or "the value last committed to source control", `BUILD` = `$(Build.BuildId)`, `REVISION` =
`$(Rev:r)`
([Code components ALM](https://learn.microsoft.com/power-apps/developer/component-framework/code-components-alm#when-to-increment-the-major-and-minor-version)).

**Point 2 is the documented alternative and is what the Build Tools task exists for.** The ALM
Accelerator made the choice explicit: by default the pipeline assigned the version, and setting
`UseSolutionVersionFromDataverse` to `True` instead preserved the exported number downstream so it
"is reflected in your source control when the solution source is committed"
([Configure ALM Accelerator pipelines](https://learn.microsoft.com/power-platform/guidance/alm-accelerator/configure-azuredevops-pipelines)).

**Point 1 is what most maker-led teams actually do**, because it's the only step the portal offers.

### Choosing

The trade is which store is authoritative:

- **Bump at build (3)** decouples the version from the environment. Every artifact is uniquely
  identified by its pipeline run, and parallel work can't collide. The dev environment's own number
  drifts and becomes meaningless, and the version isn't known until CI runs.
- **Bump in the environment (2)** keeps Dataverse, the exported artifact, and source control in
  agreement, so "what version is dev on" has an answer. The cost is that the bump is a write to a
  shared environment: two developers exporting in parallel contend for the same counter, and the
  version line in `Solution.xml` is a predictable merge conflict.

Point 2 fits the model in §9, where deploy packs from committed source: the version in Git is the
version that gets imported, so no CI stamping step is needed. Point 3 fits DEV-as-truth pipelines
that rebuild the artifact anyway.

Whatever the scheme, the version shipped to prod should be traceable to a commit.

### Why deploy is not a bump point

Bumping at import time — incrementing as part of promoting to test, uat, or prod — looks like a
convenience and breaks two properties worth keeping.

**It destroys the identity between version and content.** A promotion pipeline runs the same source
against several targets in sequence. Bump on each and the identical content carries a different
version in every environment, so the number no longer identifies what is installed. Comparing
versions across the chain stops being meaningful, which is the main thing version numbers are read
for.

**It severs the link to the commit.** A deploy that bumps must either write the new number back to
`Solution.xml` — dirtying a working tree that promotion pipelines normally require to be clean — or
stamp it into the packed artifact alone, producing a version that exists in no commit.

Both failure modes point the same way: the bump belongs where the change is captured, not where it
is shipped. A team that needs a different version before promoting should bump at point 2 or 3 and
re-pack. The case that appears to require a deploy-time bump — a managed import where the target
already holds that version — is resolved the same way, and redeploying an earlier commit to a
rebuilt environment specifically wants the original number rather than a new one.

---

## 6. Import semantics

Four documented modes
([Solution lifecycle](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm#solution-lifecycle),
[Upgrade or update a solution](https://learn.microsoft.com/power-apps/maker/data-platform/update-solutions)):

| Mode | Deletes removed components | Notes |
|---|---|---|
| **Update** | No | Fastest. Components deleted in source stay in the target. |
| **Upgrade** (default) | Yes | Rolls up all patches. Target ends up matching source. |
| **Stage for Upgrade** | Deferred | Creates a `<Solution>_Upgrade` holding solution so both versions coexist for data migration; deletion happens on **Apply Solution Upgrade**. |
| **Patch** | No | Additive hotfix layered on the parent. Roll up later via `CloneAsSolution` + `DeleteAndPromote`. |

`pac solution import` flags that map onto these: `--stage-and-upgrade`, `--import-as-holding`,
`--settings-file`, `--skip-lower-version`, `--activate-plugins`, `--publish-changes`, `--async`,
`--force-overwrite`
([pac solution](https://learn.microsoft.com/power-platform/developer/cli/reference/solution#pac-solution-import)).

**Pending upgrades block everything.** A failed staged upgrade leaves the `<Solution>_Upgrade`
holding solution in place, and further upgrades and patches fail until it is completed or deleted.
Microsoft's guidance is to delete it immediately, fix the problem at source, and reapply
([Solution upgrade fails due to a previously pending upgrade](https://learn.microsoft.com/troubleshoot/power-platform/dataverse/working-with-solutions/upgrade-fails-pending-upgrade)).

**Overwrite Customizations** copies the incoming value into the active layer; the active layer
continues to exist. It has no effect on components with merge behavior — forms, sitemap, ribbon,
app modules
([Overwrite customizations option](https://learn.microsoft.com/power-apps/maker/data-platform/update-solutions#overwrite-customizations-option)).

**Deployment settings file.** Generate with
`pac solution create-settings --solution-zip <zip> --settings-file <json>`, fill in the target's
connection IDs and environment variable values, commit one file per environment, and pass it at
import. Without it, import prompts interactively and unattended pipelines stall
([Pre-populate connection references and environment variables](https://learn.microsoft.com/power-platform/alm/conn-ref-env-variables-build-tools)).

---

## 7. Source format: XML and YAML

Two formats, not interchangeable
([Source control file formats](https://learn.microsoft.com/power-platform/alm/use-source-control-solution-files#source-control-file-formats)):

| | XML (legacy) | YAML |
|---|---|---|
| Manifest | `Other/Solution.xml` + `Other/Customizations.xml` | `solutions/<name>/solution.yml` |
| Multi-solution repo | Not supported | Supported |
| Canvas apps, modern flows | Not supported | Supported |
| Git diffs | Verbose | Compact |

**YAML requires Power Platform Git integration. Tested, not inferred.**

- **Producing YAML:** only native Dataverse Git integration writes it. `pac solution clone`, `sync`,
  and `unpack` emit XML, confirming the Git integration FAQ's statement that these commands "don't
  currently support YAML format"
  ([Git integration FAQs](https://learn.microsoft.com/power-platform/alm/git-integration/faqs)).
- **Consuming YAML:** `pac solution pack` reads a Git-integration YAML folder and packs it, detecting
  the format from the `solutions/` subdirectory.

The `pac solution unpack` reference page claims that folders "extracted by using `pac solution
clone`" use the YAML layout, gated on Microsoft.PowerApps.CLI 2.4.1 or later
([pac solution unpack remarks](https://learn.microsoft.com/power-platform/developer/cli/reference/solution#pac-solution-unpack)).
That claim did not hold under testing — treat the page as describing `pack` support only.

Consequence: **YAML is available only to teams using Git integration.** Any `clone`/`sync`-based
toolchain, Flowline included, is on the XML format, and inherits its limits — one solution per
folder, no canvas apps, no modern flows. Adopting YAML would mean adopting Git integration and its
prerequisites (Managed Environments, Azure DevOps), not swapping a CLI flag.

Git integration constraints worth knowing before choosing it: development environments only, Azure
DevOps Git only, Managed Environments required, one branch per binding, all pending changes commit
together, and an effective 17 MB per-file limit that large canvas apps and plugin assemblies can
exceed.

---

## 8. Strategies people run

Two independent choices, plus techniques that plug into either. Conflating them is easy and
unhelpful: the automation mechanism says *how* a solution moves between environments; the
source-of-truth model says *which direction* it moves and *what form* it moves in. Every combination
is buildable.

### 8.1 Automation mechanism

**A. Manual export/import.** A maker exports a solution zip; an admin imports it. No source control.
Workable for one app with one maker.

**B. Git integration + Pipelines in Power Platform.** Bind the dev solution to an Azure DevOps repo,
commit from the maker portal, deploy through in-product pipelines with approvals and gated
deployments ([Pipelines](https://learn.microsoft.com/power-platform/alm/pipelines)). Requires
Managed Environments. The only route to the YAML source format (§7). The reasonable default for
maker-heavy organizations.

**C. Your own build pipeline.** An Azure DevOps or GitHub Actions pipeline you write and own,
running export → unpack → branch → PR → pack → checker → import. Microsoft ships first-party tasks
and actions for the Dataverse steps, so the work is defining the pipeline, not scripting `pac`
calls: [Power Platform Build Tools](https://learn.microsoft.com/power-platform/alm/devops-build-tools)
for Azure DevOps, [GitHub Actions for Power Platform](https://learn.microsoft.com/power-platform/alm/devops-github-actions)
for GitHub. Standard for pro-dev teams shipping plugins and PCF. Most flexible, most to maintain.

**D. ALM Accelerator for Power Platform.** A canvas app over Build Tools pipelines — the C machinery
with a maker-friendly UI on top. **Deprecated on Microsoft Learn**
([overview](https://learn.microsoft.com/power-platform/guidance/alm-accelerator/overview)); migrate
toward B or C rather than starting here.

### 8.2 Source-of-truth model

**DEV as truth** — Microsoft's model, and what §2 through §6 describe. The unmanaged solution lives
in a development environment, source control holds its unpacked form, and everything downstream
receives managed. Golden rules 1 and 6 (§3) state it directly.

**PROD as truth** — prod holds the unmanaged solution and plays the role master plays in Git. A
development environment is a branch: copy prod down, change it, merge back by deploying to prod,
then re-provision for the next change
([Everyone got ALM wrong in Dynamics 365/Dataverse](https://automatevalue.com/blog/everyone-got-alm-wrong-in-dynamics-365-dataverse/)).
A community position, not Microsoft guidance. It is Flowline's default; §9 covers what it costs.

Of the golden rules in §3, this model breaks **rule 6** only — nothing managed is exported or
deployed. **Rule 1 still holds**: prod is the baseline, not the workspace, and customization still
happens in a development environment. Prod plays the role master plays in Git, and you do not commit
to master directly either.

The axes are orthogonal. A PROD-as-truth shop still needs a mechanism from 8.1 — usually C, because
B's Git integration is built for the DEV-as-truth direction and binds development environments only.

### 8.3 Techniques

Not strategies — practices that slot into any mechanism above.

- **Code-first push tools** — spkl, Daxif, PACX, Flowline. These target §4 steps 4–5: registering
  assemblies and steps from attributes or config in the repo instead of clicking through the Plug-in
  Registration Tool, making step registration source-controlled and idempotent. They handle the code
  half; solution transport still runs through pack and import.
- **Deployment settings files** (§6) — the prerequisite for unattended import of any solution
  carrying connection references or environment variables.
- **Solution checker** as a build gate (§4 step 12).
- **Table segmentation** (§3) for large solutions.
- **Environment provisioning automation** — scripted create/copy/reset, which PROD-as-truth needs
  continuously and DEV-as-truth needs for per-branch environments.
- **Capture and transport solutions** (§10) — a preferred solution as the capture net, plus
  sprint-sized transport solutions for fast promotion.

---

## 9. The single-party unmanaged model

Strategies A–D assume the managed layer is doing work: protecting one party's customizations from
another party's deployment. When one organization authors everything and the repository is the
source of truth, there is no second party, and the managed layer's protection converts into a
failure mode — a hotfix applied directly to prod creates an active layer, and subsequent managed
deployments silently stop landing on that component.

Deploying unmanaged removes that failure mode. The incoming definition overwrites, deterministically,
and what is in source is what is in the environment. Three costs move onto the tooling in exchange.

**Deletion does not propagate.** Unmanaged import is additive: "you add all the components of that
solution into your default solution. You can't delete the components by uninstalling the solution"
([Overview of working with solutions](https://learn.microsoft.com/dynamics365/customerengagement/on-premises/customize/solutions-overview?view=op-9-1)).
Overwrite is not replace. A column, view, or plugin step removed in dev stays in the target
indefinitely. Managed **Upgrade** is the only import mode that deletes removed components, so an
unmanaged pipeline needs an explicit diff-and-delete path.

**Solutions provide no rollback.** "Changes applied by importing an unmanaged solution cannot be
uninstalled. Do not install an unmanaged solution if you want to roll back the changes"
([Create, export, or import an unmanaged solution](https://learn.microsoft.com/dynamics365/customerengagement/on-premises/developer/create-export-import-unmanaged-solution?view=op-9-1#import-an-unmanaged-solution)).
Re-importing the previous version from Git reverts changed components and does nothing about
components the bad deploy added. Real rollback is an environment backup taken before deploy.

**Merge-behavior components do not overwrite.** Forms, sitemap, ribbon, and app modules merge on
unmanaged import: a solution carrying diff FormXml merges with the target's active customizations
rather than replacing them
([Form ALM](https://learn.microsoft.com/power-platform/alm/form-alm)). A field added to a prod form
survives a deploy that omits it, and `Overwrite Customizations` does not reach these component
types. Correcting form drift means removing the active layer for that form.

**Drift is detected and reset, not prevented.** Rule 1 still governs (§8.2) — customization belongs
in a development environment — but the Power Platform makes that rule hard to enforce, and this
model removes the backstop that would otherwise make a violation visible. In the managed model, an
edit made directly in prod creates an active layer above the managed one: the platform records it as
a distinct layer, and the next deployment silently fails to land on that component, which is at
least a symptom. With everything unmanaged there is one layer, and it is also the baseline, so a
direct prod edit is indistinguishable from legitimate state until something compares it against
source.

That turns the discipline into a loop rather than a prohibition:

1. **Detect** — compare the environment's components against committed source and report what is
   present but undeclared.
2. **Decide** — absorb the change into source, or discard it.
3. **Reset** — when a development, test, or UAT environment has diverged too far to reconcile,
   re-provision it from prod rather than merging it back.

The re-provision step is what makes the model tolerant of rule 1 being broken in practice: branches
are cheap and disposable, so a drifted environment is replaced instead of repaired. It works only
while prod itself stays clean, which is why detection runs against prod too.

One consequence for deploy ordering survives regardless: unmanaged import overwrites without warning
or undo, so deploying from a source that predates an unabsorbed prod change destroys that change.
Detection before deploy, not sync-on-every-deploy, is the guard.

---

## 10. Capture and transport patterns

Two solution roles fall out of §9. They are complementary, not alternatives: one solution holds the
state, and small solutions optionally carry changes.

### 10.1 Capture — the state solution

If the unmanaged layer *is* the whole set of customizations, then one solution should aim to contain
all of them. That solution is what syncs to source control, what `drift` compares against, and what
orphan cleanup needs (§10.3).

**Preferred solution** is the platform's capture mechanism: set it, and objects you create land there
instead of *Common Data Services Default Solution*
([Set a preferred solution](https://learn.microsoft.com/power-apps/maker/data-platform/preferred-solution)).
The adoption guidance frames it as capture-for-transport — makers set it so that "the promoted
solution contains all the required assets… preparing the assets to be ALM-ready"
([Tenant environment strategy](https://learn.microsoft.com/power-platform/guidance/adoption/environment-strategy)).
Component membership is not exclusive: "when a component is already part of an existing unmanaged
solution, it will still be added to the preferred solution," which is what makes it usable as a net
rather than an owner.

**How much it captures is not settled.** The documentation is written around object *creation*.
Field experience on this project is broader — adding a column to an out-of-box table records the
change in the preferred solution — though that case is also explainable as creation, since the
column is a new component even when its table is not. The discriminating test is a change that
creates no new component at all: adding an existing field to an out-of-box form, retuning an
out-of-box view, or editing the sitemap. Until that is tested, treat edit-capture as unverified
rather than as either documented behavior or a known gap.

Documented limitations, which apply regardless:

- Components created in the classic solution explorer never enter the preferred solution, and the
  setting can't be viewed or changed there.
- Unsupported for Dataverse for Teams, cards, dataflows, AI Builder, chatbots, connections,
  gateways, custom connectors, Power Automate flows (limited), and canvas apps created from an image
  or a Figma design.

Because coverage has holes either way, capture is a first line, not a guarantee. Drift detection
(§9) remains the backstop that catches whatever the net misses.

### 10.2 Transport — sprint solutions

Unmanaged solutions are transport units. They create no layers, they are "simply containers," and a
component can belong to several at once
([Best practices for ALM in Dynamics 365 applications](https://learn.microsoft.com/dynamics365/guidance/implementation-guide/application-lifecycle-management-product),
[Introduction to solutions](https://learn.microsoft.com/dynamics365/customerengagement/on-premises/developer/introduction-solutions?view=op-9-1)).
Solution membership is a view over the unmanaged layer, not an ownership claim, so components can be
regrouped freely — the operation that is expensive and disruptive with managed solutions.

That makes a **solution per sprint** practical: package the sprint's changed components, promote
dev → test → uat → prod, discard it. Import time scales with component count, so a sprint-sized
transport is fast where a full-state solution is not.

The platform already accepts this shape. **Patches** are additive-only, cannot delete components,
and exist for exactly this purpose. Sprint transports generalize patch semantics and drop the
parent-version lineage.

**Why the same pattern fails managed.** Each managed sprint solution becomes its own layer over the
components it touches. Layer order is install order and conflicts resolve "last one wins" (§1), so
after enough sprints the effective value of a component is the product of a stack nobody can reason
about, and old sprint solutions can't be removed without taking their components with them.
Unmanaged sprint solutions have no layer identity, so the stack never accumulates.

Related: last-write-wins is usually the behavior teams want. The alternative — a new change landing
in a managed layer and being invisibly shadowed by an older unmanaged customization above it (§1) —
is the failure mode that generates support tickets.

**Governance is still required.** A stream of diffs is order-dependent in a way full-state import is
not: full state is roughly idempotent, while two transports touching the same component produce
different results depending on arrival order. Sprint cadence supplies the ordering discipline, and a
record of which transport reached which environment supplies the audit trail. Ad-hoc transports
without either reintroduce the problem this section is solving.

### 10.3 Why both roles are needed

Declarative deletion requires complete state. `drift`-style comparison and orphan cleanup work by
treating *absent from source* as *deleted*; in a transport solution, absent means *unchanged*. The
two readings cannot come from one artifact.

So the state solution stays authoritative — it is what source control holds, what comparison runs
against, and what drives deletion — while transports are an optional accelerator for getting a
specific change through the pipeline quickly. Transports never drive reconciliation.

An alternative worth evaluating: compare two *environments* directly rather than source against
environment. That removes the dependency on the state solution being complete, and answers questions
source-versus-environment cannot, such as how far test has diverged from prod.

### 10.4 What managed solutions are actually for

Managed solutions are usually presented as hygiene — the responsible way to ship. That framing
obscures what the mechanism does. **Every capability unique to managed solutions is an ownership
mechanism**: it establishes who authored a definition, who may change it, and whose value wins when
two parties disagree.

| Mechanism | Ownership question it answers |
|---|---|
| Managed layers | Who supplied this value? Layer identity records authorship, and "See solution layers" is an authorship audit. |
| [Managed properties](https://learn.microsoft.com/power-platform/alm/managed-properties-alm) | Who may change it? The author marks components non-customizable so consumers can't modify them. |
| Delete only by uninstalling the owning solution | Who may remove it? Deletion authority stays with the author. |
| Dependency tracking that blocks uninstall | Who depends on me? Protects a base owner from consumers breaking underneath them. |
| Publisher and prefix | Whose component is this? Authorship in the name. |
| The unmanaged layer always winning (§1) | Who has final say? The environment owner overrides every vendor. |

Read that way, the "cost" in §1 — an unmanaged customization silently shadowing managed updates — is
not a defect. It is customer sovereignty, working as designed. The customer's own layer outranks
everything shipped to them.

This is the reason the model exists, and it is the reason it feels like pure overhead in a
single-author organization: you are paying for arbitration between parties that don't exist.

**The prerequisite is disjoint ownership, not team count.** Layers attach to components, so managed
protects you when ownership is *partitioned* — the base team owns these tables, the app team owns
those. Two teams that genuinely co-own the same component get no boundary from managed; they get a
stack resolved by install order. That is the usual reason enterprise managed setups degrade: managed
is adopted for "multiple teams," the teams' components overlap, and the result is layer archaeology
rather than protection.

**Where the boundary can live instead.** Ownership can be enforced in source control — branch
protection, code owners, review gates — whenever the parties share a repository and a pipeline. Two
internal scrum teams usually do. An external partner, a separate business unit with its own tenant,
or a purchased ISV product does not. The sharpest form of the rule:

> Use managed solutions when you must enforce ownership across a trust boundary you cannot govern
> with shared process.

Everything else is a preference, and it should be priced against the costs in §1 and §6 — shadowed
updates, blocked uninstalls, holding solutions, components that can't be moved between solutions,
and layer archaeology. The other drivers for splitting large programs — independent deploy cadence,
blast-radius isolation, sheer size — are real but do not by themselves require *managed*; §10.2
transports address cadence and size without it
([Organize your solutions](https://learn.microsoft.com/power-platform/alm/organize-solutions)).

---

## 11. Flowline's mapping

Flowline is a PROD-as-truth toolchain (§8.2) with code-first push (§8.3) for the code half, and
stands in for a mechanism of its own rather than plugging into B, C, or D. Its command surface
(`src/Flowline/Program.cs`):

| Command | Role in the loop |
|---|---|
| `init` | §4 — create an empty unmanaged solution and publisher in a DEV environment, then scaffold the repo. Greenfield entry point. |
| `clone` | §4 — initialize a project from an existing Dataverse solution: unpack solution XML, scaffold the Plugins and WebResources projects. |
| `push` | §4 steps 3–5 — build and register the plugin assembly and web resources directly into DEV, reading `[Step]` attributes to create or update registrations. Skips pack and import. |
| `sync` | §4 steps 7–8 and §5 point 2 — bump the version in DEV, then export and unpack to source-controlled XML. |
| `deploy` | §4 steps 14–16 — pack from the repo and import into test, uat, prod, or a URL. |
| `provision` | §2 — create a DEV, TEST, or UAT environment by copying from production. |
| `drift` | §9 — compare committed source against a live environment and report components present there but not declared in source. Read-only; same comparison `deploy` runs, without the deletion. |
| `generate` | Early-bound C# types from solution entities and custom APIs. |
| `status` | Environments, connection state, solution version, PAC auth, git state. |

The §9 drift loop maps onto two commands: `drift` detects (read-only, runs against prod as well as
the lower environments), and `provision` resets by copying a DEV, TEST, or UAT environment from
production. Repair is not a step — a diverged branch environment is replaced.

How the product answers §9's three costs, verified against the source:

- **Deletion propagation — covered.** `OrphanCleanupService.CompareAsync` compares committed source
  against the target's components; `deploy` deletes what source no longer declares, `--no-delete`
  suppresses it, and `drift` runs the same comparison read-only
  (`Commands/DriftCommand.cs:48`, `Commands/DeployCommand.cs:681`). Deletion is disabled in managed
  mode, where the platform's Upgrade already removes components (`ResolveRunMode`: `noDelete ||
  includeManaged ? RunMode.NoDelete`).
- **Solution checker — pre-import gate.** `SolutionCheckService` runs via `RunPreImportAsync`, so it
  gates the import rather than reporting after it, and it also runs under `--dry-run`
  (`Commands/DeployCommand.cs:238`). It fails on Critical findings only
  (`Services/SolutionCheckService.cs:21`), matching platform enforcement, and `--skip-solution-check`
  bypasses it. `PacUtils.CheckSolutionAsync` passes no ruleset, exclusions, or overrides
  (`Utils/PacUtils.cs:257`), so results remain valid for Managed Environment enforcement — a
  constraint to preserve deliberately if rule-exclusion options are ever added. Sync-time checking
  is not offered; per §4 that is the right default, with an opt-in flag the reasonable extension.
- **Versioning — bump point 2 (§5).** `sync` writes the new version to the DEV solution *before*
  exporting, so the downloaded XML already carries it (`Commands/SyncCommand.cs:99`). `--bump`
  takes `patch` (default), `minor`, `major`, or `none`; `patch` increments the **build** segment and
  zeroes revision (`BumpVersion`, `Commands/SyncCommand.cs:188`), leaving `revision` unused and free
  for a CI stamp. The semver-style names sit over Dataverse's four-part
  `major.minor.build.revision`, so `patch` is not the fourth segment.
- **Managed/unmanaged source — unmanaged by default.** `PacUtils` passes
  `--packagetype Both` only when `includeManaged` is set, otherwise `Unmanaged`
  (`Utils/PacUtils.cs:221`); `sync --managed` is the opt-in. This overrides the `pac solution sync`
  default of `Both` described in §4.
- **Deployment settings file — not implemented.** No `--settings-file` or `create-settings` usage
  exists in `src/`. Solutions carrying connection references or environment variables therefore need
  their target values supplied another way before `deploy` can run unattended against them.

Two questions §10 leaves open for the product:

- **Sprint transports (§10.2) are not modelled.** Flowline assumes one solution per project, which is
  the §10.1 state role. Supporting transports means a second artifact kind whose import must never
  trigger orphan cleanup, since absence in a transport does not mean deletion (§10.3).
- **Environment-to-environment comparison (§10.3).** `drift` compares committed source against one
  environment. Comparing two environments directly would answer how far test has diverged from prod
  without depending on the state solution being complete.

The format question in §7 stays open and depends on the pinned PAC version.
