<div align="center">
  <h1><strong>ClientConfig</strong></h1>
  <p>Capture each connecting player's client ConVars (crosshair, sensitivity, viewmodel, name, …) and store the latest snapshot per player for website display.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/github/stars/yappershq/ClientConfig-Modsharp?style=flat&logo=github" alt="Stars">
</p>

---

A ModSharp (CS2 / Source 2) plugin. On connect it fires a staggered batch of `QueryConVar` requests for every ConVar in its list, collects the replies, and upserts a per-player JSON snapshot into the **PlayerAnalytics** database — one row per player, overwritten on every reconnect. There are no commands; it runs passively in the background.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/ClientConfig.Core/` | `<sharp>/modules/ClientConfig.Core/` |
| `.assets/configs/clientconfig.json.example` | `<sharp>/configs/clientconfig.json` |

Restart the server (or change map) to load. The config is also auto-written with defaults on first run if absent. Reuses PlayerAnalytics' DB credentials file (no duplicated creds) but opens its own connection, so there is no runtime load-order dependency.

## ⚙️ Configuration

`configs/clientconfig.json`:

| Setting | Default | Meaning |
|---------|---------|---------|
| `enabled` | `true` | Master switch |
| `analyticsDatabaseConfig` | `playeranalytics.database.jsonc` | Config file to pull DB credentials from |
| `settleDelaySeconds` | `5` | Force-flush timeout for clients that never finish answering |
| `queriesPerFrame` | `8` | Stagger rate for the connect-time query burst |
| `serverTag` | `""` | Optional tag stored on each snapshot |
| `convars` | crosshair / sensitivity / viewmodel / radar / HUD / name set | The ConVars to capture (extend freely) |

## 🔧 How it works

On post-auth connect (real players only — bots/HLTV skipped) the plugin fires a staggered batch of `QueryConVar` requests, one stagger group of `queriesPerFrame` per frame, so a large list doesn't slam the client's reliable channel. Replies arrive on the main game thread and are collected into a per-client accumulator (no locking needed). Once every reply lands — or `settleDelaySeconds` elapses for a client that never finishes — the snapshot is copied to a plain dictionary and the DB upsert runs off the game thread. ConVars the client lacks are stored as `<CvarNotFound>` for diagnostics.

### `client_configs` schema

| Column | Type | Notes |
|--------|------|-------|
| `SteamId` | `BIGINT` | Primary key — one row per player |
| `Name` | `VARCHAR(128)` | Last known in-game name |
| `ConfigJson` | `LONGTEXT` | JSON object: `{ "convar": "value", … }` |
| `ServerTag` | `VARCHAR(64)` | Which server captured it (from config, optional) |
| `CapturedAt` | `DATETIME` | UTC capture time |

The table is CodeFirst-created on load. The connection pool is capped (`Maximum Pool Size=4`) for shared-DB friendliness.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs `.build/modules/ClientConfig.Core/ClientConfig.dll` plus its bundled dependencies (SqlSugar, MySqlConnector).

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
