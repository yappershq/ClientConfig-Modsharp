using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ClientConfig.Configuration;

/// <summary>
/// Analytics DB credentials block. Matches PlayerAnalytics' <c>{ "Database": { ... } }</c> config
/// file shape so ClientConfig reuses the same credentials instead of duplicating them.
/// </summary>
public sealed class DatabaseConfig
{
    [JsonPropertyName("type")]     public string Type     { get; set; } = "mysql";
    [JsonPropertyName("host")]     public string Host     { get; set; } = "localhost";
    [JsonPropertyName("port")]     public int    Port     { get; set; } = 3306;
    [JsonPropertyName("database")] public string Database { get; set; } = "player_analytics";
    [JsonPropertyName("user")]     public string User     { get; set; } = "root";
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;

    // Wrapper matching PlayerAnalytics' { "Database": { ... } } config file shape.
    private sealed class AnalyticsDbFile
    {
        [JsonPropertyName("database")] public DatabaseConfig? Database { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,                     // analytics uses PascalCase keys (Type/Host/…)
        ReadCommentHandling         = JsonCommentHandling.Skip, // .jsonc
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Reuse an existing DB config already on the server (default PlayerAnalytics' database config)
    /// so ClientConfig never duplicates credentials. Returns null if the file is missing/unparseable.
    /// </summary>
    public static DatabaseConfig? LoadShared(string sharpPath, string fileName, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", fileName);
        try
        {
            if (!File.Exists(path))
            {
                logger.LogError("[ClientConfig] Shared DB config '{Path}' not found — set 'analyticsDatabaseConfig' or create it", path);
                return null;
            }

            var db = JsonSerializer.Deserialize<AnalyticsDbFile>(File.ReadAllText(path), JsonOpts)?.Database;
            if (db is null || string.IsNullOrWhiteSpace(db.Host))
            {
                logger.LogError("[ClientConfig] '{Path}' has no usable Database section", path);
                return null;
            }

            logger.LogInformation("[ClientConfig] Using DB config from {File}", fileName);
            return db;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[ClientConfig] Failed to read shared DB config '{Path}'", path);
            return null;
        }
    }
}

/// <summary>
/// ClientConfig plugin config (configs/clientconfig.json). Controls whether capture is active, which
/// DB credentials file to reuse, how long to wait for the client to answer before flushing a partial
/// snapshot, and the list of client ConVars to query on connect.
/// </summary>
public sealed class ClientConfigConfig
{
    /// <summary>Master switch. When false the connect listener does nothing.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    /// Existing server config file (in configs/) to pull the analytics DB credentials from, so
    /// ClientConfig needs no DB creds of its own. Defaults to PlayerAnalytics' database config.
    /// </summary>
    [JsonPropertyName("analyticsDatabaseConfig")]
    public string AnalyticsDatabaseConfig { get; set; } = "playeranalytics.database.jsonc";

    /// <summary>
    /// Seconds to wait after the LAST outstanding query is satisfied before forcing a flush. Also the
    /// hard ceiling: if a client never answers some cvars, the snapshot still flushes after this many
    /// seconds from the first query (a cvar the client lacks DOES reply with CvarNotFound, so this is
    /// mainly a safety net for a half-disconnected client). Default 5s.
    /// </summary>
    [JsonPropertyName("settleDelaySeconds")] public int SettleDelaySeconds { get; set; } = 5;

    /// <summary>
    /// How many QueryConVar net messages to fire per game frame. Staggers the connect-time burst so a
    /// ~100-cvar list does not slam the client's reliable channel in one frame. Default 8.
    /// </summary>
    [JsonPropertyName("queriesPerFrame")] public int QueriesPerFrame { get; set; } = 8;

    /// <summary>Optional server tag stored alongside each snapshot (which server captured it).</summary>
    [JsonPropertyName("serverTag")] public string ServerTag { get; set; } = "";

    /// <summary>The client ConVars to capture on connect. Seeded with the cybershoke-style player set.</summary>
    [JsonPropertyName("convars")] public List<string> ConVars { get; set; } = DefaultConVars();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
    };

    public static ClientConfigConfig Load(string sharpPath, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", "clientconfig.json");
        try
        {
            if (!File.Exists(path))
            {
                var def = new ClientConfigConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts));
                logger.LogInformation("[ClientConfig] Wrote default config to {Path}", path);
                return def;
            }

            var cfg = JsonSerializer.Deserialize<ClientConfigConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                logger.LogError("[ClientConfig] clientconfig.json deserialized to null — using defaults");
                return new ClientConfigConfig();
            }

            // An empty list in the file would silently capture nothing — fall back to defaults.
            if (cfg.ConVars.Count == 0)
                cfg.ConVars = DefaultConVars();

            return cfg;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[ClientConfig] Failed to load clientconfig.json — using defaults");
            return new ClientConfigConfig();
        }
    }

    /// <summary>The cybershoke-style player config set. Admin can extend via the config file.</summary>
    public static List<string> DefaultConVars() =>
    [
        // Crosshair
        "cl_crosshaircolor", "cl_crosshaircolor_r", "cl_crosshaircolor_g", "cl_crosshaircolor_b",
        "cl_crosshairsize", "cl_crosshairthickness", "cl_crosshairgap", "cl_crosshairstyle",
        "cl_crosshairdot", "cl_crosshairalpha", "cl_crosshairusealpha",
        "cl_crosshair_drawoutline", "cl_crosshair_outlinethickness", "cl_crosshair_recoil",
        "cl_crosshair_t", "cl_fixedcrosshairgap",
        "cl_crosshair_dynamic_maxdist_splitratio", "cl_crosshair_dynamic_splitalpha_innermod",
        "cl_crosshair_dynamic_splitalpha_outermod", "cl_crosshair_dynamic_splitdist",

        // Mouse / sensitivity
        "sensitivity", "zoom_sensitivity_ratio", "m_pitch", "m_yaw",

        // Viewmodel
        "viewmodel_fov", "viewmodel_offset_x", "viewmodel_offset_y", "viewmodel_offset_z",
        "viewmodel_presetpos", "cl_prefer_lefthanded",

        // Radar
        "cl_radar_scale", "cl_radar_rotate", "cl_radar_always_centered",
        "cl_radar_icon_scale_min", "cl_hud_radar_scale", "cl_radar_square_with_scoreboard",

        // HUD
        "cl_hud_color", "hud_scaling", "cl_color", "cl_showloadout",
        "cl_show_clan_in_death_notice", "cl_teammate_colors_show", "cl_use_opens_buy_menu",
        "cl_teamcounter_playercount_instead_of_avatars",

        // Grenade crosshair
        "cl_silencer_mode", "cl_grenadecrosshair", "cl_grenadecrosshairdelay",

        // Misc
        "name", "volume", "voice_vox", "snd_mixahead", "mm_dedicated_search_maxping",
        "cl_allow_animated_avatars",
    ];
}
