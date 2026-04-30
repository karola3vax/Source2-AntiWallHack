using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;

namespace S2FOW.Core;

/// <summary>
/// Minimal unmanaged layout used to walk Source 2's client list. These fields must
/// match the engine memory layout from S2FOW gamedata.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct S2FowCUtlMemory
{
    public nint* m_pMemory;
    public int m_nAllocationCount;
    public int m_nGrowSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct S2FowCUtlVector
{
    public int m_iSize;
    public S2FowCUtlMemory m_Memory;

    public nint this[int index]
    {
        get => m_Memory.m_pMemory[index];
        set => m_Memory.m_pMemory[index] = value;
    }
}

/// <summary>
/// Sends crash-recovery full updates to individual viewers.
///
/// The actual engine action is small: S2FOW finds the server-side client for a
/// player slot and writes -1 to m_nForceWaitForTick. Source 2 treats that as a
/// request to send that viewer a fresh full update.
/// </summary>
internal sealed class NetworkFullUpdateService
{
    private readonly S2FowNetworkServerService _networkServerService;

    private NetworkFullUpdateService(S2FowNetworkServerService networkServerService)
    {
        _networkServerService = networkServerService;
    }

    public static bool TryCreate(out NetworkFullUpdateService? service, out string error)
    {
        service = null;
        error = string.Empty;

        try
        {
            var networkServerService = new S2FowNetworkServerService();
            if (networkServerService.Handle == nint.Zero)
            {
                error = "NetworkServerService_001 was not available.";
                return false;
            }

            service = new NetworkFullUpdateService(networkServerService);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryForceFullUpdate(CCSPlayerController? player, out string error)
    {
        error = string.Empty;

        if (player == null || !player.IsValid)
        {
            error = "player is null or invalid";
            return false;
        }

        try
        {
            var networkGameServer = _networkServerService.GetIGameServer();
            var client = networkGameServer.GetClientBySlot(player.Slot);
            if (client == null)
            {
                error = $"network client not found for slot {player.Slot}";
                return false;
            }

            client.ForceFullUpdate();
            TryRefreshPawnInterpolation(player);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryRefreshPawnInterpolation(CCSPlayerController player)
    {
        try
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                return;

            pawn.Teleport(null, pawn.EyeAngles, null);
        }
        catch
        {
            // The full update is the crash-recovery path. Same-angle teleport is only
            // a best-effort visual refresh and should not make the update fail.
        }
    }
}

/// <summary>Wraps the engine interface that exposes the current game server.</summary>
internal sealed class S2FowNetworkServerService : NativeObject
{
    private readonly VirtualFunctionWithReturn<nint, nint> _getGameServerFunc;

    public S2FowNetworkServerService()
        : base(NativeAPI.GetValveInterface(0, "NetworkServerService_001"))
    {
        _getGameServerFunc = new VirtualFunctionWithReturn<nint, nint>(
            Handle,
            GameData.GetOffset("INetworkServerService_GetIGameServer"));
    }

    public S2FowNetworkGameServer GetIGameServer()
    {
        return new S2FowNetworkGameServer(_getGameServerFunc.Invoke(Handle));
    }
}

/// <summary>Wraps the engine game server so S2FOW can find a client by player slot.</summary>
internal sealed class S2FowNetworkGameServer : NativeObject
{
    private static readonly int SlotsOffset = GameData.GetOffset("INetworkGameServer_Slots");
    private readonly S2FowCUtlVector _slots;

    public S2FowNetworkGameServer(nint handle)
        : base(handle)
    {
        _slots = Marshal.PtrToStructure<S2FowCUtlVector>(Handle + SlotsOffset);
    }

    public S2FowServerSideClient? GetClientBySlot(int slot)
    {
        if (slot < 0 || slot >= _slots.m_iSize)
            return null;

        nint clientHandle = _slots[slot];
        return clientHandle == nint.Zero ? null : new S2FowServerSideClient(clientHandle);
    }
}

/// <summary>
/// Wraps one server-side client. ForceFullUpdate writes the engine field that tells
/// Source 2 to send a fresh full update to that viewer.
/// </summary>
internal sealed class S2FowServerSideClient : NativeObject
{
    private static readonly int ForceWaitForTickOffset = GameData.GetOffset("CServerSideClient_m_nForceWaitForTick");

    public S2FowServerSideClient(nint handle)
        : base(handle)
    {
    }

    public unsafe void ForceFullUpdate()
    {
        *(int*)(Handle + ForceWaitForTickOffset) = -1;
    }
}
