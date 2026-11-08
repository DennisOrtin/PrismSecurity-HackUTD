# Incident Command

An offline command center for responding to a cyber incident from a single clean laptop sitting on a
compromised network. One responder documents the incident, tracks what data is affected, and acts on the
network only through typed, human-approved actions. A local Nemotron agent proposes; a second Nemotron agent
reviews; nothing runs without a person.

Stack: SpacetimeDB 2.14.1 (C# module, bound to 127.0.0.1) · WPF on .NET 8 · two console services
(Agent, Executor) · a posture helper · a Docker sim network on a second laptop.

## Layout

| Path | What it is | Owner |
| --- | --- | --- |
| `server/` | SpacetimeDB module: tables (all private), reducers, role-checked views, audit chain | A |
| `src/IncidentCommand.Client/` | Shared: vault crypto, TOTP, settings, connection helpers, generated bindings | B |
| `src/IncidentCommand.App/` | WPF dashboard | B |
| `src/IncidentCommand.Agent/` | `Agent.exe agent` (planner) and `Agent.exe reviewer` | B |
| `src/IncidentCommand.Executor/` | The only process allowed out: runs approved actions and terminal commands over SSH | A |
| `src/IncidentCommand.PostureHelper/` | Posture checks with fix and undo, elevated per click | A |
| `sim/` | Docker hosts, `attack.ps1`, `reset.ps1`, `fingerprints.ps1` (sim laptop) | A |
| `scripts/` | Responder-laptop setup and build scripts | Both |

## Responder laptop: first-time setup

1. Install the .NET 8 SDK and the SpacetimeDB CLI 2.14.1.
2. Turn `server/` into a module project. In a scratch folder run
   `spacetime init --lang csharp --project-path server-init incident-command`,
   copy the generated module `.csproj` (and any `global.json`) into `server/`, and do NOT copy its `Lib.cs`.
3. `scripts\start-server.ps1` in its own window (loopback only).
4. `scripts\publish.ps1`, then `scripts\generate-bindings.ps1`.
5. `dotnet new sln -n IncidentCommand` then `dotnet sln add (Get-ChildItem src -Recurse -Filter *.csproj)`.
6. `scripts\build.ps1`, then run `dist\IncidentCommand.exe`. With no accounts it walks you through creating the
   incident commander (password + authenticator app).
7. Edit `%LOCALAPPDATA%\IncidentCommand\settings.json`: `ModelBaseUrl`, `ModelName`, and `SpacetimeKeyDir`.

After every module change: `publish.ps1` (add `-Wipe` to reset data) then `generate-bindings.ps1`, on both machines.

## Hour-2 checkpoint (do this first)

- [ ] Module publishes with no errors.
- [ ] Bindings generate; the solution builds.
- [ ] App starts, first-run creates `commander-01`, the main window opens.
- [ ] Overview: open an incident. The banner shows it, Timeline shows "Incident opened by commander-01".
- [ ] Settings: Verify the chain says "Chain intact".

## Services: three local AI agents and the executor

| Service | Job | Can |
| --- | --- | --- |
| Response agent (`Agent.exe agent`) | Chats with the responder and proposes actions | Propose plans, answer, suggest shell commands |
| Reviewer (`Agent.exe reviewer`) | Checks every proposed action and suggested command before a human can send it, for danger and prompt injection | Attach warnings only; never blocks |
| Parser (`Agent.exe parser`) | Reads everything (events, chat, timeline, action and terminal output) and keeps the structured record current | Write the AI summary and classification, indicators, scope proposals, timeline drafts |
| Executor (`Executor.exe`) | Runs approved catalog actions and human terminal commands over SSH | The only process with network access and SSH keys |

All AI runs locally in LM Studio. Each agent has its own model, chosen by the commander in **Settings > AI** and
applied on the next call; one question can also override the agent's model in the Timeline ("Answer with").
Defaults: agent = Nemotron 3.5 Bolt E4B, reviewer and parser = Qwen3 4B Instruct 2507 (one loaded copy serves both).
The models offered come from `Ai.Models` in `%LOCALAPPDATA%\IncidentCommand\settings.json`; each `Id` must match
LM Studio exactly. `dist\Agent.exe test` lists what LM Studio has loaded and times a reply from every configured model.
In LM Studio, turn reasoning/thinking off where a model offers it, so each answer takes seconds.

They need no setup. `run-full.bat` (or `start-services.bat`) starts all four; each waits until the incident
commander logs in, which creates their identities automatically. The commander can switch any of them off in
**Settings > Built-in services**: the identity becomes inactive, so the database refuses everything it tries.

**Actions need registered hosts.** The AI never has credentials; only the executor does, and only for hosts listed
under **Settings > Hosts** (name, address, port, pinned SSH fingerprint from `sim\fingerprints.ps1`). With no hosts
registered, the agent can only answer questions.

For the executor, `scripts\setup-keys.ps1` creates the break-glass SSH key; copy the printed public key to the sim laptop.

## Sim laptop (Windows 11, Docker Desktop with WSL2)

1. Save the responder's public key as `sim\keys\authorized_keys`.
2. `sim\reset.ps1` builds and starts `web-01`, `file-01`, `db-01`, `c2`.
3. `sim\fingerprints.ps1` prints name, tailnet address, port and fingerprint for each host.
   Enter them in the app under Settings > Hosts.
4. `sim\attack.ps1 -Fast` for rehearsal, `sim\attack.ps1` for the demo. `reset.ps1` between runs.

In the app, propose Follow logs (auth and app) on `web-01` and `file-01` first, so events stream in.

## Proving containment and eradication to the judges

The dashboard says what was done; `sim\status.ps1` proves it, by checking live inside each container.

1. Sim laptop: `reset.ps1`, then start your attack. Its first step should be `& "$PSScriptRoot\stage-implant.ps1"`
   (before anything is encrypted; it takes the backup). This plants an implant on file-01 and web-01 that beacons
   to the c2 container every 10 seconds, a cron persistence file, and an attacker SSH key.
2. Put `sim\status.ps1` on a second screen. Everything starts red.
3. In the dashboard, follow the app logs: the implant's "beacon ok to <c2 ip>:8080" lines stream in.
4. Contain: block the c2 IP (or isolate the host), kill the implant process, lock jdoe.
   **REACH C2 -> BLOCKED, IMPLANT -> stopped, jdoe -> LOCKED**, and the logs switch to "beacon FAILED".
   The agent can confirm with `check_egress` (prints BLOCKED).
5. Eradicate: `quarantine_file` on `/usr/local/bin/kworker-update`, `/etc/cron.d/kworker-update` and
   `/home/jdoe/.ssh/authorized_keys`. **PERSISTENCE and PLANTED KEY -> removed.**
6. Recover: `restore_from_backup` on `/srv/hr`. **ENCRYPTED FILES -> 0.**

## Inspecting the database

`db.bat` reads every table, including private ones, as the database owner via `spacetime sql`:

| Command | Does |
| --- | --- |
| `db.bat` | List tables and row counts |
| `db.bat all` | Dump everything |
| `db.bat incident` | Dump one table |
| `db.bat response_action -Watch` | Re-dump every 2 seconds while you click around the UI |
| `db.bat -Sql "SELECT * FROM message WHERE incident_id = 1"` | Any query |

## Not compiled yet: check these in hour 0-2

The skeleton was written against the SpacetimeDB 2.x docs but has not been compiled. The module's
SHA-256 was checked against a reference implementation. Most likely friction points:

1. **Views** (`server/Views.cs`): returning `List<T>` from a `ViewContext` view, and the typed
   `q.From.<ViewName>()` subscriptions in `Client/Stdb.cs`. Check the generated `RemoteTables` for exact names.
2. **Reducer callback signatures** in `App/Session.cs` (`WireErrors`): one discard per reducer argument.
   Compare with the generated `RemoteReducers`.
3. **Reducer status pattern** in `Client/Stdb.cs` (`Status.Committed`, `Status.Failed(var m)`).
4. **`Insert` returning the row** with its auto-incremented id (`OpenIncident`, `SubmitPlan`, `ProposeAction`).
5. **CLI flags**: `spacetime start --listen-addr`, `spacetime publish ... --server`.
6. **SSH.NET**: `FingerPrintSHA256` and `ExitStatus` on the installed version.
7. **Model runtime**: tool calling must work for Nemotron 3.5 Nano 4B on your local server.
