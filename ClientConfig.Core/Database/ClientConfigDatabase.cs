using System;
using System.Threading.Tasks;
using ClientConfig.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace ClientConfig.Database;

/// <summary>
/// Self-contained SqlSugar access to the analytics DB for the <c>client_configs</c> table. Reuses
/// PlayerAnalytics' credentials (its config file), but its own capped connection pool. Owns the
/// table: CodeFirst-creates it and upserts one snapshot row per player. All calls run off the game
/// thread.
/// </summary>
internal sealed class ClientConfigDatabase : IDisposable
{
    private readonly ILogger<ClientConfigDatabase> _logger;
    private SqlSugarScope?                          _db;

    public bool IsConnected => _db is not null;

    public ClientConfigDatabase(ILogger<ClientConfigDatabase> logger) => _logger = logger;

    public bool Connect(DatabaseConfig cfg)
    {
        try
        {
            var dbType = cfg.Type.ToLowerInvariant() switch
            {
                "mysql"      => DbType.MySql,
                "postgresql" => DbType.PostgreSQL,
                _            => throw new NotSupportedException($"Unsupported DB type '{cfg.Type}' (mysql|postgresql)"),
            };

            // Cap pool size — many plugins share the same MySQL box; default (100) exhausts max_connections.
            var conn = dbType switch
            {
                DbType.MySql => $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};User={cfg.User};Password={cfg.Password};AllowPublicKeyRetrieval=true;Maximum Pool Size=4;Minimum Pool Size=0;",
                _            => $"Host={cfg.Host};Port={cfg.Port};Database={cfg.Database};Username={cfg.User};Password={cfg.Password};Maximum Pool Size=4;Minimum Pool Size=0;",
            };

            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = dbType,
                ConnectionString      = conn,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });

            // Probe the connection so a bad config fails loudly at load instead of on first capture.
            _ = _db.Ado.GetInt("SELECT 1");
            _logger.LogInformation("[ClientConfig] Connected to analytics DB {Host}/{Db}", cfg.Host, cfg.Database);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[ClientConfig] Failed to connect to analytics DB — capture disabled");
            _db = null;
            return false;
        }
    }

    /// <summary>Create the client_configs table if missing.</summary>
    public void EnsureTable()
    {
        if (_db is null) return;
        try
        {
            _db.CodeFirst.InitTables<ClientConfigRow>();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[ClientConfig] Could not ensure client_configs table");
        }
    }

    /// <summary>
    /// Upsert the latest snapshot for a player (one row per SteamId, overwritten on reconnect).
    /// Manual find-then-update/insert keyed on the single PK — sidesteps SqlSugar's flaky
    /// Storageable/compound-key upsert behaviour (see project memory).
    /// </summary>
    public async Task UpsertAsync(long steamId, string? name, string configJson, string? serverTag)
    {
        if (_db is null) return;
        try
        {
            var tag = string.IsNullOrEmpty(serverTag) ? null : serverTag;
            var now = DateTime.UtcNow;

            var exists = await _db.Queryable<ClientConfigRow>()
                                  .Where(r => r.SteamId == steamId)
                                  .AnyAsync()
                                  .ConfigureAwait(false);

            if (exists)
            {
                await _db.Updateable<ClientConfigRow>()
                         .SetColumns(r => new ClientConfigRow
                         {
                             Name       = name,
                             ConfigJson = configJson,
                             ServerTag  = tag,
                             CapturedAt = now,
                         })
                         .Where(r => r.SteamId == steamId)
                         .ExecuteCommandAsync()
                         .ConfigureAwait(false);
            }
            else
            {
                var row = new ClientConfigRow
                {
                    SteamId    = steamId,
                    Name       = name,
                    ConfigJson = configJson,
                    ServerTag  = tag,
                    CapturedAt = now,
                };
                await _db.Insertable(row).ExecuteCommandAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[ClientConfig] Failed to upsert snapshot for {Steam}", steamId);
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }
}
