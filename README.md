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

## Services (agent, reviewer, executor)

They are built in and need no setup. `run-full.bat` (or `start-services.bat`) starts all three; each waits
until the incident commander logs in to the app, which creates their identities automatically and hands
each one a DPAPI-protected token. They reconnect on their own if the database restarts or a token is replaced.

The commander can switch any of them off in **Settings > Built-in services**. A switched-off service keeps
running but its identity is inactive, so the database refuses everything it tries and shows it no data.
"Replace service identities" revokes and recreates all three (use it if a token may have leaked).

For the executor, `scripts\setup-keys.ps1` creates the break-glass SSH key; copy the printed public key to the
sim laptop. Manual enrollment (`Agent.exe enroll agent CODE`) still works as a fallback but isn't needed.

## Sim laptop (Windows 11, Docker Desktop with WSL2)

1. Save the responder's public key as `sim\keys\authorized_keys`.
2. `sim\reset.ps1` builds and starts `web-01`, `file-01`, `db-01`, `c2`.
3. `sim\fingerprints.ps1` prints name, tailnet address, port and fingerprint for each host.
   Enter them in the app under Settings > Hosts.
4. `sim\attack.ps1 -Fast` for rehearsal, `sim\attack.ps1` for the demo. `reset.ps1` between runs.

In the app, propose Follow logs (auth and app) on `web-01` and `file-01` first, so events stream in.

## AI providers and models

Which provider (Local = LM Studio, Cloud = NVIDIA Brev) and which model each agent uses is stored in the
database and switched live by the commander in **Settings > AI**. Any single question can also pick a model
in the Timeline screen ("Answer with"). Cloud mode shows a red warning in the banner: development only.

Where each provider lives and what each model is called there is in
`%LOCALAPPDATA%\IncidentCommand\settings.json` under `Ai.Local` and `Ai.Cloud` (`BaseUrl`, `ApiKey`,
`NanoModel`, `LightningModel`). Copy the exact model IDs from LM Studio's server tab and your Brev deployment.

Check both before relying on them: `dist\Agent.exe test` (or `test local` / `test cloud`).

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
