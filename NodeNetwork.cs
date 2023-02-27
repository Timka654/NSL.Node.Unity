using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.UDP.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public class NodeNetwork<TRoomInfo> : IRoomInfo, INodeNetwork, IDisposable
    where TRoomInfo : GameInfo
{
    public NodeBridgeClient bridgeClient;

    public NodeRoomClient roomClient;

    private RoomStartInfo roomStartInfo;

    private UDPServer<UDPNodeServerNetworkClient> udpBindingPoint;

    private string udpEndPointConnectionUrl;

    private ConcurrentDictionary<Guid, NodeClient> connectedClients = new ConcurrentDictionary<Guid, NodeClient>();

    /// <summary>
    /// Can set how transport all data - P2P, Proxy, All
    /// default: All
    /// </summary>
    public NodeTransportModeEnum TransportMode { get; set; } = NodeTransportModeEnum.ProxyOnly;

    /// <summary>
    /// 1 unit = 1 second
    /// for no wait connections set this value to default = 0
    /// </summary>
    public int MaxNodesWaitCycle { get; set; } = 10;

    /// <summary>
    /// Receive transport servers from bridge server delay before continue
    /// </summary>
    public int WaitBridgeDelayMS { get; set; } = 10_000;

    public bool DebugPacketIO { get; set; } = true;

    public event OnChangeRoomStateDelegate OnChangeRoomState = state =>
    {
    };

    public event OnChangeNodesReadyDelegate OnChangeNodesReady = (current, total) => { };
    public event OnChangeNodesReadyDelayDelegate OnChangeNodesReadyDelay = (current, total) => { };

    public NodeRoomStateEnum CurrentState { get; private set; }

    public TRoomInfo RoomInfo { get; private set; }

#if DEBUG

    private void DebugOnChangeRoomState(NodeRoomStateEnum state)
    {
        LogHandle(LoggerLevel.Debug, $"{nameof(NodeNetwork<TRoomInfo>)} change state -> {state}");
    }

#endif

    internal async void Initialize(RoomStartInfo startupInfo, CancellationToken cancellationToken = default)
        => await InitializeAsync(startupInfo, cancellationToken);

    internal async Task InitializeAsync(RoomStartInfo startupInfo, CancellationToken cancellationToken = default)
    {
#if DEBUG
        OnChangeRoomState -= DebugOnChangeRoomState;
        OnChangeRoomState += DebugOnChangeRoomState;
#endif
        RoomInfo = Activator.CreateInstance(typeof(TRoomInfo), this) as TRoomInfo;

        roomStartInfo = startupInfo;

        OnChangeRoomState -= OnChangeState;
        OnChangeRoomState += OnChangeState;

        try
        {
            var connectionPoints = await initBridges(cancellationToken);

            await initUDPBindingPoint(cancellationToken);

            await initRooms(connectionPoints, cancellationToken);

            await waitNodeConnection(cancellationToken);

        }
        catch (TaskCanceledException)
        {
            Dispose();
        }

    }

    public bool Ready { get; private set; }

    private void OnChangeState(NodeRoomStateEnum state)
    {
        CurrentState = state;
        Ready = state == NodeRoomStateEnum.Ready;
    }

    private async Task<IEnumerable<RoomSessionInfoModel>> initBridges(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bridgeClient = new NodeBridgeClient(
            this,
            LogHandle,
            OnChangeRoomState,
            roomStartInfo);

        return await bridgeClient.Initialize(cancellationToken);
    }

    private Task initUDPBindingPoint(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TransportMode.HasFlag(NodeTransportModeEnum.P2POnly))
        {
            //throw new Exception("Commented code");
            this.udpBindingPoint = BaseUDPNode.CreateUDPEndPoint(
                this,
                point => udpEndPointConnectionUrl = point?.ToString(),
                LogHandle);
        }

        return Task.CompletedTask;
    }

    #region Room

    private Task initRooms(IEnumerable<RoomSessionInfoModel> connectionPoints, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        roomClient = new NodeRoomClient(
            this,
            LogHandle,
            OnChangeRoomState,
            roomStartInfo,
            connectionPoints,
            udpEndPointConnectionUrl);

        roomClient.OnExecute += roomClient_OnExecute;


        roomClient.OnTransport += roomClient_OnTransport;

        roomClient.OnChangeNodeList = (roomServer, data, instance) =>
        {
            foreach (var item in data)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var nodeClient = connectedClients.GetOrAdd(item.NodeId, id => new NodeClient(item, roomServer, this, instance));

                if (nodeClient.State == NodeClientStateEnum.None)
                {

                    if (!nodeClient.TryConnect(item))
                        throw new Exception($"Cannot connect");
                }
            }

            OnChangeNodesReady(data.Count(), roomStartInfo.TotalPlayerCount);
        };

        return roomClient.Initialize(cancellationToken);
    }

    private void roomClient_OnExecute(InputPacketBuffer buffer)
    {
        Invoke((PlayerInfo)null, buffer);
    }

    private void roomClient_OnTransport(Guid playerId, InputPacketBuffer buffer)
    {
        if (!connectedClients.TryGetValue(playerId, out var client))
            return;

        Invoke(client.PlayerInfo, buffer);
    }

    #endregion

    private async Task waitNodeConnection(CancellationToken cancellationToken)
    {
        OnChangeRoomState(NodeRoomStateEnum.WaitConnections);

        bool valid = false;

        do
        {
            await Task.Delay(100, cancellationToken);

            for (int i = 0; (i < MaxNodesWaitCycle || MaxNodesWaitCycle == 0) && connectedClients.Count < roomStartInfo.TotalPlayerCount - 1; i++)
            {
                await Task.Delay(200, cancellationToken);

                OnChangeNodesReadyDelay(i + 1, MaxNodesWaitCycle);
            }

            if (!valid)
                valid = await roomClient.SendReady(roomStartInfo.TotalPlayerCount, connectedClients.Select(x => x.Key));

        } while (!Ready);
    }

    #region Transport

    public bool Broadcast(Action<OutputPacketBuffer> builder, ushort code)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.Transport(builder, code); });

        return true;
    }

    public bool Broadcast(Action<OutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.Transport(builder); });

        return true;
    }

    public bool SendTo(NodeClient node, Action<OutputPacketBuffer> builder, ushort code)
    {
        if (!Ready)
            return false;

        node.Transport(builder, code);

        return true;
    }

    public bool SendTo(NodeClient node, Action<OutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        node.Transport(builder);

        return true;
    }

    public bool SendTo(Guid nodeId, Action<OutputPacketBuffer> builder)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            SendTo(node, builder);

        return false;
    }

    public bool SendTo(Guid nodeId, Action<OutputPacketBuffer> builder, ushort code)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            SendTo(node, builder, code);

        return false;
    }

    #endregion


    #region Execute


    public void Execute(ushort command, Action<OutputPacketBuffer> build)
    {
        var packet = new OutputPacketBuffer();

        packet.WriteUInt16(command);

        build(packet);

        packet.WithPid(RoomPacketEnum.Execute);

        SendToRoomServer(packet);
    }

    public void SendToRoomServer(OutputPacketBuffer packet)
    {
        roomClient.SendToServers(packet);
    }

    #endregion


    #region Handle


    private Dictionary<ushort, ReciveHandleDelegate> handles = new Dictionary<ushort, ReciveHandleDelegate>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RegisterHandle(ushort code, ReciveHandleDelegate handle)
    {
        if (!handles.TryAdd(code, handle))
            throw new Exception($"code {code} already contains in {nameof(handles)}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReciveHandleDelegate GetHandle(ushort code)
    {
        if (handles.TryGetValue(code, out var handle))
            return handle;

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invoke(PlayerInfo nodePlayer, InputPacketBuffer buffer)
    {
        var code = buffer.ReadUInt16();

        var handle = GetHandle(code);

        if (handle == null)
            return;

        handle(nodePlayer, buffer);
    }

    public virtual void Invoke(Action action, InputPacketBuffer buffer)
    {
        action();
    }

    #endregion


    #region IRoomInfo

    public void Broadcast(OutputPacketBuffer packet)
    {
        Parallel.ForEach(connectedClients, c => { c.Value.Send(packet, false); });

        packet.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PlayerInfo GetPlayer(Guid id)
    {
        if (connectedClients.TryGetValue(id, out var node))
            return node.PlayerInfo;

        return null;
    }

    public void SendTo(Guid nodeId, OutputPacketBuffer packet)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            SendTo(node.PlayerInfo, packet);
    }

    public void SendTo(PlayerInfo player, OutputPacketBuffer packet, bool disposeOnSend = true)
    {
        player.Network.Send(packet, disposeOnSend);
    }

    #endregion

    protected virtual void LogHandle(LoggerLevel level, string content)
    {
    }

    public void Dispose()
    {
        if (RoomInfo is IDisposable d)
            d.Dispose();

        bridgeClient?.Dispose();
        roomClient?.Dispose();
        udpBindingPoint?.Stop();

        foreach (var item in connectedClients)
        {
            item.Value.PlayerInfo.Network.Dispose();
        }
    }
}