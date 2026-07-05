# Flowline CLI — Working Status Messages
## Requirements Document

**Product**: Flowline CLI
**Feature**: Animated working status messages

---

## 1. Overview

Flowline CLI displays animated working status messages during long-running operations. The goal is to keep the user informed, entertained, and reassured that the process is alive — without lying about what is actually happening internally.

The design is inspired by Claude Code's working status style (`✻ Manifesting…`) but themed around Dataverse, Power Platform, ALM, and pop culture references.

---

## 2. Design Principles

- **Honest, not fake.** Status messages reflect the elapsed time, not fabricated internal stages. We do not know what Dataverse is doing inside a PAC CLI call.
- **Elapsed timer is the heartbeat.** Every status message is shown alongside a ticking elapsed duration (e.g. `✻ Dataverse is thinking… (elapsed 1:23)`). The timer keeps the screen alive between status changes — filler statuses are not needed.
- **Status changes are meaningful.** Because the timer handles the "is it alive?" signal, the status message only changes when there is something new to say (tone shift, escalation, pop culture beat). Frequent churn for its own sake is avoided.
- **Tone matches the action.** Each action has a distinct personality:
  - **Clone** — ceremonial, dramatic (one-time init)
  - **Push** — snappy, routine (many times a day, inner steps are known)
  - **Sync** — routine, mildly resigned (many times a day)
  - **Deploy** — tense, high-stakes (rare, matters a lot)
  - **Provision** — epic, slow-burn comedy (longest operation)
- **Hard timeout at 60 minutes.** All PAC CLI operations are polled synchronously. PAC CLI times out after 60 minutes. All timelines cap at 3600s with a dedicated timeout message per action.

---

## 3. Status Message Format

```
✻ {StatusMessage}… (elapsed {m}:{ss})
```

Examples:
```
✻ Asking prod for a solution… (elapsed 0:07)
✻ Dataverse said "hold on"… (elapsed 0:23)
✻ We're gonna need a bigger timeout… (elapsed 3:04)
```

On completion:
```
✓ {CompletionMessage} ({elapsed})
```

On timeout / error:
```
✗ {TimeoutMessage}
```

---

## 4. Polling & Update Behaviour

- PAC CLI is called synchronously per action.
- Status is polled every **3–4 seconds**.
- Each poll returns an elapsed duration which is rendered alongside the status message.
- On each tick: find the latest milestone beat where `AtSeconds <= elapsed` and display it. The beat holds until the next milestone fires.
- No random status selection. Messages appear in a fixed, ordered sequence.
- **Pre-timeout warning zone**: from 3300s (55m) onward, show escalating urgency beats.
- At 3600s (60m): show the timeout error message and abort.

---

## 5. Three-Layer Status System

Status messages operate in three layers, checked in order on each tick:

**Layer 1 — Easter Eggs** (named industry people)
Fire once every 5 **total** runs (persisted across sessions), only after 10 minutes elapsed, deterministic rotation. Never replace a milestone beat — fire between them. See section 12.

**Layer 2 — Round-Number Messages**
Fire once per run at exact elapsed milestones (5m, 10m, 15m, 30m, 60m). See section 11.

**Layer 3 — Timeline Milestones + Alternates**
The main ordered sequence. Each beat has a primary and 1–2 alternates. Rotate through alternates by persisted total run count per action. See sections 6–9.

---

## 6. Push — Stage-Based (Inner Steps Known)

Push is the exception. Because Flowline controls the inner steps, push uses **stage-based statuses** rather than elapsed-time-based ones. Each stage fires exactly once as the step completes.

Each stage has a pool of alternates to rotate across repeat runs so the developer isn't reading the same line 10 times a day.

| Stage | Primary | Alt 1 | Alt 2 |
|---|---|---|---|
| Connect | ✻ Knocking on dev's door… | ✻ Back again… | ✻ Knight Rider mode activated… *(Knight Rider)* |
| Diff | ✻ Diffing web resources… | ✻ Scanning for changes… | ✻ What has changed, Mr. Anderson… *(Matrix)* |
| Hash assemblies | ✻ Hashing assemblies… | ✻ Computing… | ✻ Does not compute… just kidding… |
| Upload web resources | ✻ Uploading JavaScript nobody asked for… | ✻ Transferring files… | ✻ Uploading at modem speed… *(C64 nostalgia)* |
| Register plugin | ✻ Registering the plugin (again)… | ✻ Registering… | ✻ I'll be back to register this… *(Terminator)* |
| Update SDK steps | ✻ Updating SDK steps… | ✻ Wiring things up… | ✻ Reconfiguring the matrix… *(Matrix)* |
| Publish | ✻ Publishing customizations… | ✻ Still publishing (it's always publishing)… | ✻ Go ahead, make my deploy… *(Dirty Harry)* |
| Refresh cache | ✻ Asking the cache to please refresh… | ✻ Clearing the cobwebs… | ✻ Rebooting reality… |
| Done | ✓ Shipped 🚢 | ✓ Done. See you in 10 minutes… | ✓ Yippee-ki-yay. Pushed. *(Die Hard)* |

### Push — Repeat Run Openers

Rotate the opening line on repeat runs. Track push count using the persisted total run counter for push. Display `Push number {n} today…` based on runs within the current calendar day (derive from a persisted last-run date).

- ✻ Back again…
- ✻ Push number {n} today…
- ✻ You must really like pushing…
- ✻ Same plugin, different vibe…
- ✻ Even Tony Stark iterates this fast… *(Marvel)*
- ✻ It works on my machine. Let's verify that…
- ✻ The documentation says this should be fast…

---

## 7. Clone — Elapsed-Based Timeline

Clone is a one-time init action. Tone is ceremonial and dramatic. Expected duration: **2–30 minutes**, occasionally longer for large solutions.

| Elapsed | Primary | Alt 1 | Alt 2 |
|---|---|---|---|
| 0s (0m) | ✻ Initiating first contact with prod… | — | — |
| 10s (0m) | ✻ Introducing Flowline to Dataverse… | — | — |
| 20s (0m) | ✻ One does not simply clone a solution… *(LOTR)* | — | — |
| 40s (0m) | ✻ Prod is sizing us up… | — | — |
| 60s (1m) | ✻ Export job accepted. The journey begins… | — | — |
| 90s (1m) | ✻ This is the way… *(Mandalorian)* | ✻ I'll be back… said the export job… *(Terminator)* | — |
| 120s (2m) | ✻ Prod is really thinking about this one… | — | — |
| 180s (3m) | ✻ We're gonna need a bigger timeout… *(Jaws)* | — | — |
| 240s (4m) | ✻ There is no spoon… or progress bar… *(Matrix)* | ✻ Computer says no… we said please… *(Little Britain)* | — |
| 300s (5m) | ✻ Still exporting. Big solution energy… | ✻ The maker portal shows nothing. Classic. | — |
| 420s (7m) | ✻ I love it when a plan comes together… any minute now… *(A-Team)* | ✻ Microsoft said "it depends"… | — |
| 540s (9m) | ✻ You shall not pass… yet… *(LOTR)* | ✻ Roads? Where we're going we don't need roads… *(BTTF)* | — |
| 600s (10m) | ✻ Still here. So is prod. We bond… | ✻ Have you tried turning Dataverse off and on again?… | — |
| 720s (12m) | ✻ Hasta la vista, progress bar… *(Terminator)* | ✻ Even the Batcomputer would've finished by now… *(DC)* | ✻ Stack Overflow has no answer for this… |
| 900s (15m) | ✻ This is the way. The very long way… *(Mandalorian)* | ✻ The answer is always "it depends"… | — |
| 1080s (18m) | ✻ MacGyver would've been done by now… *(MacGyver)* | ✻ GitHub Copilot suggested a full rewrite… | — |
| 1200s (20m) | ✻ Al Bundy sold more shoes than this takes seconds… *(MWC)* | ✻ Somewhere, a low-code developer is judging us… | — |
| 1380s (23m) | ✻ The A-Team built a tank. Still cloning… *(A-Team)* | ✻ Azure SLA: 99.9% uptime. Response time: negotiable… | — |
| 1500s (25m) | ✻ I feel the need… the need for speed… *(Top Gun)* | ✻ Winter came. Still cloning… *(GoT)* | — |
| 1680s (28m) | ✻ Come with me if you want to clone… *(Terminator)* | ✻ Avengers, assemble. We need help with this clone… *(Marvel)* | — |
| 1800s (30m) | ✻ This solution must be the size of a planet… | ✻ Filed a support ticket. ETA: 3–5 business days… | — |
| 2100s (35m) | ✻ With great power comes great export time… *(Spider-Man)* | ✻ Beam it up already… *(Star Trek)* | ✻ This would've been a canvas app. It's not. |
| 2400s (40m) | ✻ In space no one can hear you export… *(Aliens)* | ✻ Thanos would've finished this in a snap… *(Marvel)* | — |
| 2700s (45m) | ✻ Have you tried lunch? ☕🥪… | ✻ Pair programming with patience… | — |
| 3300s (55m) | ✻ Getting nervous. PAC CLI has a limit… | — | — |
| 3480s (58m) | ✻ This is taking suspiciously long… | — | — |
| 3540s (59m) | ✻ One minute left. Come on Dataverse… | — | — |

**Completion:** `✓ Cloned. Welcome to the project. ({elapsed})`

**Timeout:** `✗ Timed out after 60 minutes. Prod didn't want to let go. Try again or check your solution size.`

---

## 8. Sync — Elapsed-Based Timeline

Sync runs many times a day. Tone is routine and mildly resigned. Expected duration: **2–30 minutes**, occasionally longer.

Sync gets the most alternates per beat — enough for roughly 3 full rotations of variety throughout the day.

Rotate the opening status on repeat runs from this pool:
- ✻ Syncing again…
- ✻ Let's see what changed…
- ✻ Let's see what dev broke…
- ✻ Pulling reality…
- ✻ Reconciling multiverses…
- ✻ Checking in on dev…

| Elapsed | Primary | Alt 1 | Alt 2 |
|---|---|---|---|
| 0s (0m) | ✻ Asking dev what changed… | ✻ Syncing again… | ✻ Let's see what dev broke… |
| 10s (0m) | ✻ Dev is thinking… (dangerous)… | ✻ Export job incoming… | ✻ Initiating sync sequence… |
| 20s (0m) | ✻ Export job: pending. As always… | ✻ Dataverse warming up… | ✻ Loading… *(C64 tape loader vibes)* |
| 40s (0m) | ✻ You shall not pass… yet… *(LOTR)* | ✻ Scanning for lifeforms… *(Star Trek)* | ✻ What has changed, Mr. Anderson… *(Matrix)* |
| 60s (1m) | ✻ Dataverse is taking the scenic route… | ✻ This is the way… *(Mandalorian)* | ✻ Roads? Where we're going we don't need roads… *(BTTF)* |
| 90s (1m) | ✻ We're not in Kansas anymore… *(Wizard of Oz)* | ✻ I know kung fu… I know syncing… *(Matrix)* | ✻ Affirmative. Still syncing… *(Terminator)* |
| 120s (2m) | ✻ Dev says "almost"… (dev is lying)… | ✻ Almost there… almost… *(Star Wars)* | ✻ Lock S-foils in waiting position… *(Star Wars)* |
| 180s (3m) | ✻ Just keep syncing, just keep syncing… *(Finding Nemo)* | ✻ Sync harder… *(Die Hard)* | ✻ I love it when a sync comes together… *(A-Team)* |
| 240s (4m) | ✻ Computer says no… *(Little Britain)* | ✻ Negative. Still processing… *(Terminator)* | ✻ Danger, Will Robinson. Sync still running… *(Lost in Space)* |
| 300s (5m) | ✻ Found drift. Of course there's drift… | ✻ Drift detected. Surprise. | ✻ The spice must flow… so must the sync… *(Dune)* |
| 420s (7m) | ✻ With great power comes great sync times… *(Spider-Man)* | ✻ Hasta la vista, fast sync… *(Terminator)* | ✻ Microsoft said "it depends"… |
| 540s (9m) | ✻ I love it when a sync comes together… *(A-Team)* | ✻ Beam it up already… *(Star Trek)* | ✻ Do or do not. There is no fast sync… *(Star Wars)* |
| 600s (10m) | ✻ Still syncing. Dev has opinions… | ✻ 10 minutes in. Dev really has opinions… | ✻ Have you tried turning Dataverse off and on again?… |
| 720s (12m) | ✻ I need your clothes, your boots, and your solution… *(Terminator)* | ✻ Scanning… still scanning… *(Robocop)* | ✻ I could do this all day… said Captain America, waiting… *(Marvel)* |
| 900s (15m) | ✻ Nobody told me there'd be syncs like these… | ✻ Airwolf could've done this by now… *(Airwolf)* | ✻ Knight Rider is faster than this… *(Knight Rider)* |
| 1080s (18m) | ✻ MacGyver would've synced by now… *(MacGyver)* | ✻ I feel the need… the need for speed… *(Top Gun)* | ✻ Even Tony Stark's servers are faster than this… *(Marvel)* |
| 1200s (20m) | ✻ Leaping… still leaping… *(Quantum Leap)* | ✻ Al Bundy sold more shoes than this takes seconds… *(MWC)* | ✻ Somewhere, a low-code developer is judging us… |
| 1500s (25m) | ✻ Winter came. Still syncing… *(GoT)* | ✻ Come with me if you want to sync… *(Terminator)* | ✻ To the Batcave! …after this finishes… *(DC)* |
| 1800s (30m) | ✻ This solution must be enormous… | ✻ Resistance is futile. So is rushing this… *(Star Trek)* | ✻ In a galaxy far, far away this sync started… *(Star Wars)* |
| 2100s (35m) | ✻ Strange things are happening… *(Doctor Strange)* | ✻ Hulk wait. Hulk not like waiting… *(Marvel)* | ✻ Azure SLA: 99.9% uptime. Response time: negotiable… |
| 2400s (40m) | ✻ I am Iron Man. And I am still waiting… *(Marvel)* | ✻ Kryptonite would be faster than this… *(DC)* | ✻ The documentation says this should be fast… |
| 2700s (45m) | ✻ Have you tried lunch? ☕🥪… | ✻ Even HAL 9000 would've finished by now… *(2001)* | ✻ Checking the Power CAT blog… still waiting… |
| 3300s (55m) | ✻ Getting nervous. PAC CLI has a limit… | — | — |
| 3480s (58m) | ✻ This is taking suspiciously long… | — | — |
| 3540s (59m) | ✻ One minute left. Come on Dataverse… | — | — |

**Completion:** `✓ Synced. ({elapsed})`

**Timeout:** `✗ Timed out after 60 minutes. Dev had too much to say. Try again.`

---

## 9. Deploy — Elapsed-Based Timeline

Deploy runs once every few days or weeks. Tone is tense and high-stakes. Expected duration: **variable**, up to 60 minutes for large solutions.

| Elapsed | Primary | Alt 1 | Alt 2 |
|---|---|---|---|
| 0s (0m) | ✻ Packing the solution. This is it… | — | — |
| 15s (0m) | ✻ Zipping 1,200 files into one… | — | — |
| 30s (0m) | ✻ Connecting to target. No turning back… | ✻ There is no spoon. Only deployment… *(Matrix)* | — |
| 60s (1m) | ✻ Uploading. Committed… | ✻ I'll be back. With a deployed solution… *(Terminator)* | — |
| 90s (1m) | ✻ Import job accepted. The easy part is over… | — | — |
| 120s (2m) | ✻ Dataverse is thinking really hard… | ✻ Scanning target environment… *(Terminator)* | — |
| 180s (3m) | ✻ I'll be back… said the import job… *(Terminator)* | ✻ Roads? Where we're going we deploy first… *(BTTF)* | — |
| 240s (4m) | ✻ Houston, we have an import… *(Apollo 13)* | ✻ Lock S-foils in deploy position… *(Star Wars)* | — |
| 300s (5m) | ✻ May the deploy be with you… *(Star Wars)* | ✻ Yippee-ki-yay, deploying… *(Die Hard)* | ✻ Microsoft said "it depends"… |
| 420s (7m) | ✻ We do not break prod. We do not break prod… | ✻ Stay on target… stay on target… *(Star Wars)* | — |
| 540s (9m) | ✻ Holding the line… | ✻ Game over, man? No. Not yet… *(Aliens)* | — |
| 600s (10m) | ✻ Still importing. Breathe… | ✻ I know what you're thinking… did it deploy? *(Dirty Harry)* | ✻ Have you tried turning Dataverse off and on again?… |
| 720s (12m) | ✻ The A-Team would've been done by now… *(A-Team)* | ✻ Affirmative. Still importing… *(Terminator)* | ✻ The maker portal shows nothing. Classic. |
| 900s (15m) | ✻ I am Groot. I am also still deploying… *(GotG)* | ✻ Danger, Will Robinson. Deploy still running… *(Lost in Space)* | ✻ The answer is always "it depends"… |
| 1080s (18m) | ✻ Resistance is futile. And apparently so is speed… *(Star Trek)* | ✻ Avengers, assemble. We need help with this deploy… *(Marvel)* | ✻ Stack Overflow has no answer for this… |
| 1200s (20m) | ✻ MacGyver could've deployed with a paperclip by now… *(MacGyver)* | ✻ Leaping… still leaping… *(Quantum Leap)* | ✻ Somewhere, a low-code developer is judging us… |
| 1500s (25m) | ✻ In space no one can hear you deploy… *(Aliens)* | ✻ Dormammu, I've come to bargain for a faster deploy… *(Doctor Strange)* | — |
| 1680s (28m) | ✻ Come with me if you want to deploy… *(Terminator)* | ✻ Thanos would've finished this in a snap… *(Marvel)* | — |
| 1800s (30m) | ✻ Publishing customizations. The final stretch. Probably… | ✻ Even HAL 9000 would be done by now… *(2001)* | ✻ Filed a support ticket. ETA: 3–5 business days… |
| 2100s (35m) | ✻ We are Groot. We are also still deploying… *(GotG)* | ✻ Beam it up already… *(Star Trek)* | ✻ This would've been a canvas app. It's not. |
| 2400s (40m) | ✻ Crossing fingers. And toes. And everything… | ✻ I am your father. And I am still waiting… *(Star Wars)* | ✻ GitHub Copilot suggested a full rewrite… |
| 3300s (55m) | ✻ Getting nervous. PAC CLI has a limit… | — | — |
| 3480s (58m) | ✻ This is taking suspiciously long… | — | — |
| 3540s (59m) | ✻ One minute left. Come on Dataverse… | — | — |

**Completion:** `✓ Deployed. Don't break prod. 🎉 ({elapsed})`

**Timeout:** `✗ Timed out after 60 minutes. We do not break prod… but we did run out of time. Check the import status in the maker portal.`

---

## 10. Provision — Elapsed-Based Timeline

Provision is the longest and most unpredictable operation. It involves a full database backup and copy on the Microsoft side. Tone is epic slow-burn comedy. Expected duration: **30–120 minutes**.

| Elapsed | Primary | Alt 1 | Alt 2 |
|---|---|---|---|
| 0s (0m) | ✻ Asking Microsoft for an environment… | — | — |
| 10s (0m) | ✻ Microsoft put us in the queue… | — | — |
| 30s (0m) | ✻ Found a worker somewhere in Azure… | ✻ Scanning for available compute… *(Terminator)* | — |
| 60s (1m) | ✻ Backup started. The real work begins… | — | — |
| 120s (2m) | ✻ Backing up the database… | ✻ Downloading more RAM… *(classic IT meme)* | — |
| 180s (3m) | ✻ This is fine 🔥… | ✻ I'll be back. With a database copy. *(Terminator)* | — |
| 300s (5m) | ✻ Still backing up. Prod is chunky… | ✻ Stay on target… *(Star Wars)* | ✻ Microsoft said "it depends"… |
| 420s (7m) | ✻ Backup done. Now copying. More waiting… | — | — |
| 540s (9m) | ✻ Cloning the database. The big one… | ✻ Game over, man? No. Just backing up… *(Aliens)* | — |
| 600s (10m) | ✻ Still copying. Go make coffee ☕… | ✻ Roads? Where we're going we need more RAM… *(BTTF)* | ✻ Have you tried turning Dataverse off and on again?… |
| 720s (12m) | ✻ Copying bytes. All of them… | ✻ Resistance is futile. The database will be copied… *(Star Trek)* | ✻ The maker portal shows nothing. Classic. |
| 900s (15m) | ✻ We're gonna need a bigger VM… *(Jaws)* | ✻ In space no one can hear you provision… *(Aliens)* | ✻ Azure SLA: 99.9% uptime. Response time: negotiable… |
| 1020s (17m) | ✻ You're a wizard, Harry. A very slow wizard… *(Harry Potter)* | ✻ Hasta la vista, fast provisioning… *(Terminator)* | — |
| 1200s (20m) | ✻ I love it when a plan comes together… eventually… *(A-Team)* | ✻ Leaping… still leaping… *(Quantum Leap)* | ✻ Somewhere, a low-code developer is judging us… |
| 1380s (23m) | ✻ Coffee done? Still copying… | ✻ Knight Rider could've driven here faster… *(Knight Rider)* | — |
| 1500s (25m) | ✻ The A-Team built a tank. Azure is still copying… *(A-Team)* | ✻ Airwolf could've flown here faster… *(Airwolf)* | ✻ Stack Overflow has no answer for this… |
| 1680s (28m) | ✻ MacGyver would've provisioned with a paperclip by now… *(MacGyver)* | ✻ I feel the need… the need for speed… *(Top Gun)* | — |
| 1800s (30m) | ✻ Half an hour. You could've watched an episode of The A-Team… | ✻ I could do this all day… said Captain America, waiting… *(Marvel)* | ✻ Filed a support ticket. ETA: 3–5 business days… |
| 1980s (33m) | ✻ Scanning… still scanning… *(Robocop)* | ✻ Even Skynet provisioned faster… *(Terminator)* | ✻ Checking the Power CAT blog… still waiting… |
| 2160s (36m) | ✻ Why does it take so long? Nobody knows. Not even Satya… | ✻ Strange things are happening… *(Doctor Strange)* | ✻ This would've been a canvas app. It's not. |
| 2340s (39m) | ✻ I am Groot. I am also still copying… *(GotG)* | ✻ Danger, Will Robinson. Still copying… *(Lost in Space)* | — |
| 2520s (42m) | ✻ Al Bundy sold more shoes than this takes seconds… *(MWC)* | ✻ Come with me if you want to provision… *(Terminator)* | ✻ GitHub Copilot suggested a full rewrite… |
| 2700s (45m) | ✻ Do or do not. There is no fast provision… *(Star Wars)* | ✻ Dormammu, I've come to bargain for faster provisioning… *(Doctor Strange)* | — |
| 2880s (48m) | ✻ Beam. It. Up. *(Star Trek)* | ✻ Hulk wait. Hulk not like waiting… *(Marvel)* | ✻ The documentation says this should be fast… |
| 3060s (51m) | ✻ HAL, open the database… HAL? *(2001)* | ✻ To the Batcave! …after this finishes… *(DC)* | — |
| 3240s (54m) | ✻ The Prancing Pony called. Frodo arrived. Still copying… *(LOTR)* | ✻ In a galaxy far, far away this provision started… *(Star Wars)* | — |
| 3420s (57m) | ✻ Winter came. And went. Still copying… *(GoT)* | ✻ Even the DeLorean would've time-travelled past this by now… *(BTTF)* | — |
| 3600s (60m) | ✻ You could have learned Rust by now… | ✻ I am your father. And I am still waiting… *(Star Wars)* | — |

**Completion:** `✓ Environment ready. Microsoft delivered. ({elapsed})`

**Timeout:** `✗ Timed out after 60 minutes. Azure is still working on it. Check the environment status in the Power Platform admin center.`

> **Note:** The provision operation continues on the Microsoft side even after PAC CLI times out. Always direct the user to the Power Platform admin center to monitor progress after a timeout.

---

## 11. Round-Number Messages

These fire **once per run** at exact elapsed milestones, replacing the timeline beat for that tick only, before resuming the normal timeline. Apply to all long-running actions (clone, sync, deploy, provision).

| Elapsed | Status |
|---|---|
| 300s (5m) | ✻ Five minutes. We're committed now… |
| 600s (10m) | ✻ Ten minutes. This better be worth it… |
| 900s (15m) | ✻ Fifteen minutes. Dataverse doesn't care about your feelings… |
| 1800s (30m) | ✻ Half an hour. You could have watched an episode of The A-Team… |
| 3600s (60m) | ✻ One hour. You could have watched The Mandalorian. All of it… |

---

## 12. Easter Eggs — Named Industry People

Easter eggs reference real, named people in the Power Platform, .NET, and Microsoft ecosystem. They are insider rewards — only developers deep in the community will get them all.

### Firing Rules

- Only fire **after 10 minutes elapsed**
- Fire **maximum once every 5 runs**
- **Never replace a milestone beat** — slot between milestones only
- Rotate **deterministically** by run count so every nudge eventually gets seen
- Apply to all long-running actions: clone, sync, deploy, provision

### Easter Egg Pool

**Scott Durow** *(XrmToolBox legend, PCF pioneer, recognizable green hair)*
- ✻ Channeling Scott Durow's green hair for energy…
- ✻ Scott Durow would've shipped AND dyed his hair by now…
- ✻ Consulting the green-haired oracle…
- ✻ Even Scott Durow's hair dye dries faster than this…

**Jonas Rapp** *(FetchXML Builder, XrmToolBox contributor)*
- ✻ Jonas Rapp is probably watching this export too…
- ✻ Jonas Rapp would've written a FetchXML query for this by now…

**Guido Preite** *(Power Platform MVP, community fixture)*
- ✻ Even Guido Preite is refreshing his browser waiting for this…

**Tanguy Touzard** *(PCF controls, community tools)*
- ✻ Tanguy Touzard built a PCF control faster than this…

**Charles Lamanna** *(Microsoft CVP of Power Platform)*
- ✻ Charles Lamanna promised this would be fast. We believe him…
- ✻ Somewhere, Charles Lamanna is tweeting about low-code. We're still waiting…

**April Dunnam** *(Microsoft Power Platform advocate)*
- ✻ April Dunnam made a tutorial on waiting. It was shorter than this…

**Scott Hanselman** *(.NET community legend, Hanselminutes podcast)*
- ✻ Scott Hanselman has recorded three podcast episodes since this started…
- ✻ Hanselman would've blogged about this wait by now…
- ✻ This wait deserves a Hanselminutes episode…

**Scott Guthrie** *(Microsoft EVP Azure)*
- ✻ Scott Guthrie is spinning up a new Azure region. Still faster than this…

**Anders Hejlsberg** *(creator of C# and TypeScript)*
- ✻ Anders Hejlsberg designed C# faster than this is running…
- ✻ Anders Hejlsberg added async/await to C# for moments exactly like this…

**Satya Nadella** *(Microsoft CEO)*
- ✻ Not even Satya knows what Azure is doing right now…
- ✻ Somewhere in Redmond, nobody knows either… (not even Satya)

### Total Easter Egg Count: 18

Enough for 18 consecutive runs before the pool repeats.

---

## 13. Pop Culture Reference Index

For maintainability, a full index of all pop culture references used.

| Reference | Source |
|---|---|
| One does not simply… | Lord of the Rings |
| You shall not pass | Lord of the Rings |
| The Prancing Pony / Frodo | Lord of the Rings |
| This is the way | The Mandalorian |
| May the X be with you | Star Wars |
| Stay on target | Star Wars |
| Lock S-foils in X position | Star Wars |
| Almost there… almost… | Star Wars (Death Star trench) |
| Do or do not. There is no X | Star Wars (Yoda) |
| I am your father | Star Wars |
| In a galaxy far, far away | Star Wars |
| There is no spoon | The Matrix |
| What has changed, Mr. Anderson | The Matrix |
| I know kung fu | The Matrix |
| Reconfiguring the matrix | The Matrix |
| I'll be back | The Terminator |
| Hasta la vista | The Terminator |
| Come with me if you want to X | The Terminator |
| I need your clothes, your boots… | The Terminator |
| Affirmative. Still X… | The Terminator |
| Even Skynet provisioned faster | The Terminator |
| Scanning target environment | The Terminator |
| Roads? Where we're going… | Back to the Future |
| Even the DeLorean would've… | Back to the Future |
| I feel the need… the need for speed | Top Gun |
| Yippee-ki-yay | Die Hard |
| Sync harder | Die Hard |
| I know what you're thinking… | Dirty Harry |
| Go ahead, make my deploy | Dirty Harry |
| Game over, man? | Aliens |
| In space no one can hear you X | Aliens |
| Houston, we have a problem | Apollo 13 |
| We're gonna need a bigger X | Jaws |
| HAL, open the database | 2001: A Space Odyssey |
| Even HAL 9000 would be done | 2001: A Space Odyssey |
| Beam it up | Star Trek |
| Resistance is futile | Star Trek (Borg) |
| Scanning for lifeforms | Star Trek |
| Scanning… still scanning… | Robocop |
| Danger, Will Robinson | Lost in Space |
| Leaping… still leaping… | Quantum Leap |
| I love it when a plan comes together | The A-Team |
| MacGyver | MacGyver |
| Knight Rider could've… | Knight Rider |
| Airwolf could've… | Airwolf |
| Al Bundy sold more shoes… | Married with Children |
| You're a wizard, Harry | Harry Potter |
| I am Groot / We are Groot | Guardians of the Galaxy (Marvel) |
| Avengers, assemble | The Avengers (Marvel) |
| I am Iron Man | Iron Man (Marvel) |
| Even Tony Stark's servers… | Iron Man (Marvel) |
| Thanos would've finished in a snap | Avengers: Infinity War (Marvel) |
| With great power comes great X | Spider-Man (Marvel) |
| I could do this all day | Captain America (Marvel) |
| Hulk wait. Hulk not like waiting | The Hulk (Marvel) |
| Strange things are happening | Doctor Strange (Marvel) |
| Dormammu, I've come to bargain | Doctor Strange (Marvel) |
| Even the Batcomputer would've… | Batman (DC) |
| To the Batcave! | Batman (DC) |
| Kryptonite would be faster | Superman (DC) |
| The spice must flow | Dune |
| Winter came/is coming | Game of Thrones |
| Just keep syncing… | Finding Nemo |
| We're not in Kansas anymore | The Wizard of Oz |
| Computer says no | Little Britain |
| This is fine 🔥 | KC Green webcomic |
| Loading… | Commodore 64 tape loader |
| Downloading more RAM | Classic IT meme |
| You could have learned Rust | Developer community meme |

---

## 14. Implementation Notes

### Persistent Run Counters

Store run counters in a JSON file in the user's local app data folder (e.g. `%APPDATA%\Flowline\state.json` on Windows). Increment the counter for the relevant action at the **start** of each run. Load it at startup.

```json
{
  "runCounters": {
    "clone": 3,
    "sync": 47,
    "deploy": 6,
    "provision": 2
  },
  "lastPushDate": "2026-05-24"
}
```

```csharp
record FlowlineState(
    Dictionary<string, int> RunCounters,
    string? LastPushDate
);

// Load on startup, increment before each run, save after increment
void IncrementRunCounter(string action)
{
    var state = LoadState();
    state.RunCounters[action] = state.RunCounters.GetValueOrDefault(action, 0) + 1;
    SaveState(state);
}

int GetRunCounter(string action)
{
    return LoadState().RunCounters.GetValueOrDefault(action, 0);
}

// Daily push count — resets each new calendar day
int GetTodayPushCount()
{
    var state = LoadState();
    var today = DateTime.Today.ToString("yyyy-MM-dd");
    if (state.LastPushDate != today) return 0;
    return state.RunCounters.GetValueOrDefault("push", 0);
}
```

### Milestone Resolver (C#)

```csharp
record StatusBeat(int AtSeconds, string Status);

static string ResolveStatus(StatusBeat[] timeline, int elapsedSeconds)
{
    return timeline
        .Where(b => b.AtSeconds <= elapsedSeconds)
        .OrderByDescending(b => b.AtSeconds)
        .First()
        .Status;
}
```

### Alternate Beat Selection

Rotate through alternates deterministically by persisted total run count per action. Predictable rotation ensures every alternate gets seen eventually.

```csharp
string PickStatus(string primary, string[] alternates, int runCount)
{
    if (runCount == 0 || alternates.Length == 0) return primary;
    var all = new[] { primary }.Concat(alternates).ToArray();
    return all[runCount % all.Length];
}
```

### Easter Egg Selection

```csharp
static readonly string[] EasterEggs = [
    "Channeling Scott Durow's green hair for energy…",
    "Scott Durow would've shipped AND dyed his hair by now…",
    "Consulting the green-haired oracle…",
    "Even Scott Durow's hair dye dries faster than this…",
    "Jonas Rapp is probably watching this export too…",
    "Jonas Rapp would've written a FetchXML query for this by now…",
    "Even Guido Preite is refreshing his browser waiting for this…",
    "Tanguy Touzard built a PCF control faster than this…",
    "Charles Lamanna promised this would be fast. We believe him…",
    "Somewhere, Charles Lamanna is tweeting about low-code. We're still waiting…",
    "April Dunnam made a tutorial on waiting. It was shorter than this…",
    "Scott Hanselman has recorded three podcast episodes since this started…",
    "Hanselman would've blogged about this wait by now…",
    "This wait deserves a Hanselminutes episode…",
    "Scott Guthrie is spinning up a new Azure region. Still faster than this…",
    "Anders Hejlsberg designed C# faster than this is running…",
    "Anders Hejlsberg added async/await to C# for moments exactly like this…",
    "Not even Satya knows what Azure is doing right now…",
    "Somewhere in Redmond, nobody knows either… (not even Satya)",
];

// Fire once every 5 total runs (persisted), after 10 minutes elapsed
string? TryGetEasterEgg(int elapsedSeconds, int totalRunCount, bool easterEggFiredThisRun)
{
    if (easterEggFiredThisRun) return null;
    if (elapsedSeconds < 600) return null;
    if (totalRunCount % 5 != 0) return null;
    return EasterEggs[totalRunCount / 5 % EasterEggs.Length];
}
```

### Round-Number Messages

```csharp
static readonly StatusBeat[] RoundNumberMessages = [
    new(300,  "Five minutes. We're committed now…"),
    new(600,  "Ten minutes. This better be worth it…"),
    new(900,  "Fifteen minutes. Dataverse doesn't care about your feelings…"),
    new(1800, "Half an hour. You could have watched an episode of The A-Team…"),
    new(3600, "One hour. You could have watched The Mandalorian. All of it…"),
];
```

### Push Repeat Counter

Use the persisted total run counter for push to rotate openers. For the `Push number {n} today…` display, derive the daily count by storing a `lastPushDate` alongside the counter and resetting the daily count when the date changes.

### Timeout Handling

At 3600s elapsed, cancel the PAC CLI process and display the action-specific timeout message. For deploy and provision, append a note pointing the user to the maker portal or Power Platform admin center.

---

*Document generated from Flowline CLI design session — May 2026*
