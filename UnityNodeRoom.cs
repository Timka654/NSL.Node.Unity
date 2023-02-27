using NSL.Node.RoomServer.Shared.Client.Core;
using System;
using System.Threading;
using System.Threading.Tasks;


public class UnityNodeRoom : UnityNodeRoom<GameInfo> { }

public abstract class UnityNodeRoom<TRoomInfo> : UnityEngine.MonoBehaviour
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

    public UnityNodeNetwork<TRoomInfo> NodeNetwork { get; private set; }

    internal async void Initialize(RoomStartInfo startupInfo, CancellationToken cancellationToken = default)
        => await InitializeAsync(startupInfo, cancellationToken);

    internal async Task InitializeAsync(RoomStartInfo startupInfo, CancellationToken cancellationToken = default)
    {
        if (NodeNetwork != default)
            throw new Exception();

        NodeNetwork = new UnityNodeNetwork<TRoomInfo>()
        {
            TransportMode = TransportMode,
            MaxNodesWaitCycle = MaxNodesWaitCycle,
            WaitBridgeDelayMS = WaitBridgeDelayMS,
            DebugPacketIO = DebugPacketIO
        };

        await NodeNetwork.InitializeAsync(startupInfo, cancellationToken);
    }

    private void OnApplicationQuit()
    {
        NodeNetwork?.Dispose();
    }
}