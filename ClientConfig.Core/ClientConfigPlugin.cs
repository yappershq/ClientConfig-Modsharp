using System;
using ClientConfig.Configuration;
using ClientConfig.Database;
using ClientConfig.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;

namespace ClientConfig;

/// <summary>
/// ClientConfig — captures each connecting player's client ConVars (crosshair, sensitivity,
/// viewmodel, radar, HUD, …) and stores the latest snapshot per player in the analytics DB.
///
/// On connect it fires a staggered batch of QueryConVar requests; replies arrive on the game thread
/// and are collected into a per-client accumulator. Once all replies land (or a settle timeout
/// fires) the snapshot is serialised to JSON and upserted off the game thread.
///
/// The DB connection reuses PlayerAnalytics' credentials file (no duplicated creds) but is otherwise
/// self-contained — no cross-plugin runtime dependency, so load order doesn't matter.
/// </summary>
public sealed class ClientConfigPlugin : IModSharpModule
{
    public string DisplayName   => "ClientConfig";
    public string DisplayAuthor => "yappershq";

    private readonly ILogger<ClientConfigPlugin> _logger;
    private readonly InterfaceBridge             _bridge;
    private readonly ClientConfigConfig          _config;
    private readonly ClientConfigDatabase        _db;
    private readonly ConVarCaptureModule         _capture;

    public ClientConfigPlugin(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        var loggerFactory = sharedSystem.GetLoggerFactory();
        _logger = loggerFactory.CreateLogger<ClientConfigPlugin>();

        _bridge  = new InterfaceBridge(sharpPath, sharedSystem);
        _config  = ClientConfigConfig.Load(sharpPath, loggerFactory.CreateLogger<ClientConfigConfig>());
        _db      = new ClientConfigDatabase(loggerFactory.CreateLogger<ClientConfigDatabase>());
        _capture = new ConVarCaptureModule(_bridge, _config, _db, loggerFactory.CreateLogger<ConVarCaptureModule>());
    }

    public bool Init() => true;

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        // Reuse PlayerAnalytics' DB credentials (own connection, capped pool). CodeFirst-create our table.
        var dbConfig = DatabaseConfig.LoadShared(_bridge.SharpPath, _config.AnalyticsDatabaseConfig, _logger);
        if (dbConfig is not null && _db.Connect(dbConfig))
            _db.EnsureTable();

        _capture.Start();

        _logger.LogInformation("[ClientConfig] Loaded (DB={Db}, convars={Count})",
            _db.IsConnected, _config.ConVars.Count);
    }

    public void Shutdown()
    {
        _capture.Stop();
        _db.Dispose();
    }
}
