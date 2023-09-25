using NSL.Node.RoomServer.Shared.Client.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class UnityNodeRoom : UnityNodeRoom<GameInfo> { }

public abstract class UnityNodeRoom<TRoomInfo> : UnityEngine.MonoBehaviour, IDisposable
    where TRoomInfo : GameInfo
{
    /// <summary>
    /// Can set how transport all data - P2P, Proxy, All
    /// default: All
    /// </summary>
    public NodeTransportModeEnum TransportMode = NodeTransportModeEnum.ProxyOnly;

    /// <summary>
    /// 1 unit = 1 second
    /// for no wait connections set this value to default = 0
    /// </summary>
    public int MaxNodesWaitCycle = 10;

    /// <summary>
    /// Receive transport servers from bridge server delay before continue
    /// </summary>
    public int WaitBridgeDelayMS = 10_000;

    public bool DebugPacketIO = true;

    public UnityNodeNetwork NodeNetwork { get; } = new UnityNodeNetwork();

    internal async void Initialize(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
        => await InitializeAsync(startupInfo, cancellationToken);

    internal virtual async Task InitializeAsync(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
            cancellationToken = (cts = new CancellationTokenSource()).Token;

        NodeNetwork.TransportMode = TransportMode;
        NodeNetwork.MaxNodesWaitCycle = MaxNodesWaitCycle;
        NodeNetwork.WaitBridgeDelayMS = WaitBridgeDelayMS;
        NodeNetwork.DebugPacketIO = DebugPacketIO;

        await NodeNetwork.InitializeAsync(startupInfo, cancellationToken);
    }

    public void FillOwner(GameObject obj, Guid nodeId)
        => NodeNetwork.FillOwner(obj, nodeId);

    public void SetOwner(UnityNodeBehaviour obj, Guid nodeId)
        => NodeNetwork.SetOwner(obj, nodeId);


    CancellationTokenSource cts = default;


    private void OnDestroy()
    {
        Dispose();
    }

    private void OnApplicationQuit()
    {
        Dispose();
    }

    public void Dispose()
    {
        try
        {
            if (cts != default)
                cts.Cancel();

            NodeNetwork?.Dispose();
        }
        catch
        {

        }
    }
}