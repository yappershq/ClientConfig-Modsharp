# ClientConfig

A ModSharp (CS2 / Source 2) plugin that captures each connecting player's **client ConVars**
(crosshair, sensitivity, viewmodel, radar, HUD, …) and stores the latest snapshot per player in the
PlayerAnalytics database.

## How it works

On `OnClientPostAdminCheck` (post-auth, real players only — bots/HLTV skipped) the plugin fires a
staggered batch of `QueryConVar` requests for every ConVar in its configured list. The engine routes
each reply back through `OnClientQueryConVar` on the **main game thread**; the plugin collects them
into a per-client accumulator. Once every reply has landed — or a short settle timeout fires for a
client that never finishes answering — the snapshot is serialised to JSON and **upserted off the
game thread**.

### Threading

- `QueryConVar` is only ever called on the main game thread (it mutates a non-thread-safe cookie
  counter and sends a net message). The connect-time burst is staggered across frames via
  `IModSharp.InvokeFrameAction` (`queriesPerFrame` per frame) so a ~50-cvar list doesn't slam the
  client's reliable channel in one frame.
- The reply callback also runs on the main thread, so the per-client accumulator needs no locking.
- Only the DB upsert runs on a worker thread (`Task.Run`); the accumulator is copied to a plain
  dictionary snapshot first, so the worker never touches engine-thread state.
- The timer that enforces `settleDelaySeconds` fires on a thread-pool thread and marshals back onto
  the main thread (`InvokeFrameAction`) before reading any capture state.

## Database

Reuses PlayerAnalytics' DB credentials (read from its config file — **no duplicated credentials**)
but opens its own capped SqlSugar connection (`Maximum Pool Size=4`, the shared MySQL box is
connection-constrained). The table is CodeFirst-created in `OnAllModulesLoaded`.

### `client_configs` schema

| Column      | Type           | Notes                                            |
|-------------|----------------|--------------------------------------------------|
| `SteamId`   | `BIGINT`       | Primary key — one row per player                 |
| `Name`      | `VARCHAR(128)` | Last known in-game name                          |
| `ConfigJson`| `LONGTEXT`     | JSON object: `{ "convar": "value", … }`          |
| `ServerTag` | `VARCHAR(64)`  | Which server captured it (from config, optional) |
| `CapturedAt`| `DATETIME`     | UTC capture time                                 |

The snapshot is upserted (overwritten) on every reconnect, so the row always holds the player's
latest config. ConVars the client lacks are stored as `<CvarNotFound>` etc. for diagnostics.

## Config (`configs/clientconfig.json`)

Auto-written with defaults on first run.

| Key                       | Default                              | Meaning                                                  |
|---------------------------|--------------------------------------|----------------------------------------------------------|
| `enabled`                 | `true`                               | Master switch                                            |
| `analyticsDatabaseConfig` | `playeranalytics.database.jsonc`     | Config file to pull DB credentials from                  |
| `settleDelaySeconds`      | `5`                                  | Force-flush timeout for clients that never finish        |
| `queriesPerFrame`         | `8`                                  | Stagger rate for the connect-time query burst            |
| `serverTag`               | `""`                                 | Optional tag stored on each snapshot                     |
| `convars`                 | cybershoke-style set                 | The ConVars to capture (extend freely)                   |

## Build

```bash
dotnet build ClientConfig.slnx -c Release
```

Output module: `.build/modules/ClientConfig.Core/` (ClientConfig.dll + bundled SqlSugar/MySqlConnector).
Deploy with `modsharp-deploy . <server-profile>`.
