using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClientConfig.Configuration;
using ClientConfig.Database;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace ClientConfig.Modules;

/// <summary>
/// On client connect, queries the configured list of client ConVars via
/// <see cref="Sharp.Shared.Managers.IClientManager.QueryConVar"/>, collects the replies, and upserts
/// the snapshot to the analytics DB.
///
/// Threading model — the trickiest part:
///   * QueryConVar MUST be called on the main game thread (it mutates a non-thread-safe cookie
///     counter + dictionary and sends a net message). We trigger it from OnClientPostAdminCheck
///     (already main thread) and stagger the burst across frames via IModSharp.InvokeFrameAction.
///   * The reply callback (OnReply) is ALSO invoked on the main game thread (engine forward). So the
///     per-client accumulator is only ever touched from the main thread — NO locking needed for it.
///   * Only the final DB upsert runs off-thread (Task.Run). The accumulator is copied into a plain
///     Dictionary snapshot before handing off, so the worker never touches engine-thread state.
///
/// Completion: a capture finalises when all outstanding queries have replied, OR a per-client timer
/// fires after settleDelaySeconds (covers a half-disconnected client that never answers). The timer
/// callback marshals back onto the main thread before reading the accumulator.
/// </summary>
internal sealed class ConVarCaptureModule : IClientListener
{
    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge               _bridge;
    private readonly ClientConfigConfig            _config;
    private readonly ClientConfigDatabase          _db;
    private readonly ILogger<ConVarCaptureModule>  _logger;

    // Slot -> in-flight capture. Only ever read/written on the main game thread.
    private readonly Dictionary<int, Capture> _captures = new();

    private bool _installed;

    public ConVarCaptureModule(InterfaceBridge bridge, ClientConfigConfig config,
                               ClientConfigDatabase db, ILogger<ConVarCaptureModule> logger)
    {
        _bridge = bridge;
        _config = config;
        _db     = db;
        _logger = logger;
    }

    public void Start()
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("[ClientConfig] Disabled by config");
            return;
        }
        if (!_db.IsConnected)
        {
            _logger.LogWarning("[ClientConfig] No DB connection — capture inactive");
            return;
        }
        if (_config.ConVars.Count == 0)
        {
            _logger.LogWarning("[ClientConfig] ConVar list is empty — nothing to capture");
            return;
        }

        _bridge.ClientManager.InstallClientListener(this);
        _installed = true;
        _logger.LogInformation("[ClientConfig] Active — capturing {Count} convars on connect", _config.ConVars.Count);
    }

    public void Stop()
    {
        if (_installed)
            _bridge.ClientManager.RemoveClientListener(this);
        _installed = false;

        foreach (var c in _captures.Values)
            c.Timer?.Dispose();
        _captures.Clear();
    }

    public void OnClientPostAdminCheck(IGameClient client)
    {
        if (client.IsFakeClient || client.IsHltv)
            return;

        var slot = (int)(byte) client.Slot;

        // Replace any stale in-flight capture for this slot (fast reconnect into the same slot).
        if (_captures.TryGetValue(slot, out var stale))
        {
            stale.Timer?.Dispose();
            _captures.Remove(slot);
        }

        var capture = new Capture
        {
            SteamId    = (long) (ulong) client.SteamId,
            Name       = client.Name ?? "?",
            Outstanding = _config.ConVars.Count,
        };
        _captures[slot] = capture;

        // Snapshot the SteamID as a value type so we never deref the client from the worker.
        var steamId = client.SteamId;

        // Stagger the queries across frames to avoid a connect-time net-message burst on the client.
        var perFrame = Math.Max(1, _config.QueriesPerFrame);
        QueueQueries(slot, steamId, 0, perFrame);

        // Hard ceiling: flush whatever we have after settleDelaySeconds even if some cvars never reply.
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.SettleDelaySeconds));
        capture.Timer = new Timer(_ => OnTimeout(slot), null, timeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Fire one frame's batch of queries (main thread), then re-queue the next batch via
    /// InvokeFrameAction until the list is exhausted. The capture may be gone (player left) — bail.
    /// </summary>
    private void QueueQueries(int slot, SteamID steamId, int startIndex, int perFrame)
    {
        if (!_captures.ContainsKey(slot))
            return;

        var client = _bridge.ClientManager.GetGameClient(steamId);
        if (client is null || !client.IsValid || client.IsFakeClient)
        {
            // Client vanished mid-stagger — finalise with what we have.
            FinaliseIfPresent(slot);
            return;
        }

        var list = _config.ConVars;
        var end  = Math.Min(startIndex + perFrame, list.Count);

        for (var i = startIndex; i < end; i++)
            _bridge.ClientManager.QueryConVar(client, list[i], OnReply);

        if (end < list.Count)
            _bridge.ModSharp.InvokeFrameAction(() => QueueQueries(slot, steamId, end, perFrame));
    }

    /// <summary>QueryConVar reply — runs on the main game thread (engine forward).</summary>
    private void OnReply(IGameClient client, QueryConVarValueStatus status, string name, string value)
    {
        var slot = (int)(byte) client.Slot;
        if (!_captures.TryGetValue(slot, out var capture))
            return; // already finalised / timed out

        // Guard a fast reconnect into the same slot: a late reply from the PREVIOUS occupant must
        // not land in the new player's accumulator (which would corrupt the snapshot and prematurely
        // decrement Outstanding). Drop replies whose SteamID doesn't match the active capture.
        if ((long) (ulong) client.SteamId != capture.SteamId)
            return;

        // Only store usable values; record the failure reason for diagnostics on the others.
        capture.Values[name] = status == QueryConVarValueStatus.ValueIntact
            ? value
            : $"<{status}>";

        capture.Outstanding--;
        if (capture.Outstanding <= 0)
            Finalise(slot, capture);
    }

    private void OnTimeout(int slot)
    {
        // Timer callback is on a thread-pool thread — marshal to the main thread before touching state.
        _bridge.ModSharp.InvokeFrameAction(() => FinaliseIfPresent(slot));
    }

    private void FinaliseIfPresent(int slot)
    {
        if (_captures.TryGetValue(slot, out var capture))
            Finalise(slot, capture);
    }

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        // Flush a partial snapshot on disconnect so we don't lose the cvars we already collected.
        FinaliseIfPresent((int)(byte) client.Slot);
    }

    /// <summary>Main-thread finalise: snapshot the accumulator and hand off to an off-thread upsert.</summary>
    private void Finalise(int slot, Capture capture)
    {
        capture.Timer?.Dispose();
        _captures.Remove(slot);

        if (capture.Values.Count == 0)
            return; // nothing collected — don't write an empty row

        // Copy out of the engine-thread dictionary before the worker touches it.
        var snapshot = new Dictionary<string, string>(capture.Values);
        var json     = JsonSerializer.Serialize(snapshot);
        var steamId  = capture.SteamId;
        var name     = capture.Name;
        var tag      = _config.ServerTag;

        _ = Task.Run(() => UpsertAsync(steamId, name, json, tag));
    }

    private async Task UpsertAsync(long steamId, string name, string json, string tag)
    {
        try
        {
            await _db.UpsertAsync(steamId, name, json, tag).ConfigureAwait(false);
            _logger.LogInformation("[ClientConfig] Captured config for {Name} ({Steam})", name, steamId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[ClientConfig] Upsert failed for {Steam}", steamId);
        }
    }

    /// <summary>Per-client in-flight capture state. Touched only on the main game thread.</summary>
    private sealed class Capture
    {
        public long                       SteamId;
        public string                     Name = "?";
        public int                        Outstanding;
        public Timer?                     Timer;
        public readonly Dictionary<string, string> Values = new();
    }
}
