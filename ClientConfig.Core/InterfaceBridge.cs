using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace ClientConfig;

/// <summary>
/// Holds the engine managers ClientConfig needs. No cross-plugin interfaces are consumed — the DB
/// connection is self-contained (reuses PlayerAnalytics' credentials file, not its runtime API),
/// so there is nothing to resolve in OnAllModulesLoaded.
/// </summary>
internal sealed class InterfaceBridge
{
    internal string SharpPath { get; }

    internal IModSharp      ModSharp      { get; }
    internal IClientManager ClientManager { get; }
    internal ILoggerFactory LoggerFactory { get; }

    public InterfaceBridge(string sharpPath, ISharedSystem sharedSystem)
    {
        SharpPath     = sharpPath;
        ModSharp      = sharedSystem.GetModSharp();
        ClientManager = sharedSystem.GetClientManager();
        LoggerFactory = sharedSystem.GetLoggerFactory();
    }
}
