using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.UDP;
using NSL.UDP.Client;
using NSL.UDP.Enums;
using NSL.Utils;
using Open.Nat;
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
    public NodeRoomClient roomClient;


    public event Action<NodeInfo> OnNodeConnect = node => { };

    public event Action<NodeInfo> OnNodeDisconnect = node => { };

    public event Action OnRoomReady = () => { };

    public int TotalNodeCount { get; private set; }

    private bool NeedWaitAll { get; set; }

    private NodeSessionStartupModel roomStartInfo;

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

    public Guid LocalNodeId { get; private set; }
    public NodeClient LocalNode { get; private set; }

    public NodeNetwork()
    {
        RoomInfo = Activator.CreateInstance(typeof(TRoomInfo), this) as TRoomInfo;
    }

#if DEBUG

    private void DebugOnChangeRoomState(NodeRoomStateEnum state)
    {
        LogHandle(LoggerLevel.Debug, $"{nameof(NodeNetwork<TRoomInfo>)} change state -> {state}");
    }

#endif

    internal async void Initialize(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
        => await InitializeAsync(startupInfo, cancellationToken);

    internal async Task InitializeAsync(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
    {
#if DEBUG
        OnChangeRoomState -= DebugOnChangeRoomState;
        OnChangeRoomState += DebugOnChangeRoomState;
#endif

        roomStartInfo = startupInfo;

        LocalNodeId = Guid.Parse(roomStartInfo.Token.Split(':').First());

        OnChangeRoomState -= OnChangeState;
        OnChangeRoomState += OnChangeState;

        try
        {
            await initUDPBindingPoint(cancellationToken);

            await initRooms(startupInfo.ConnectionEndPoints, cancellationToken);

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
        if (state == NodeRoomStateEnum.Ready)
            Invoke(() => OnRoomReady());
    }

    private async Task initUDPBindingPoint(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TransportMode.HasFlag(NodeTransportModeEnum.P2POnly))
        {
            NSLEndPoint udpEndPoint = null;

            //throw new Exception("Commented code");
            this.udpBindingPoint = BaseUDPNode.CreateUDPEndPoint(
                this,
                point => udpEndPoint = point,
                LogHandle,
                roomClient_OnTransport);


            udpEndPointConnectionUrl = udpEndPoint?.ToString();

            if (udpEndPoint == null)
                return;

            var discoverer = new NatDiscoverer();

            var discoverToken = new CancellationTokenSource();

            discoverToken.CancelAfter(6_000);

            try
            {
                var device = await discoverer.DiscoverDeviceAsync(PortMapper.Pmp | PortMapper.Upnp, discoverToken);

                //var exMappings = device.GetAllMappingsAsync();
                try
                {
                    await device.CreatePortMapAsync(new Mapping(Protocol.Udp, udpEndPoint.Port, udpEndPoint.Port));
                }
                catch (MappingException mex)
                {

                }

            }
            catch (NatDeviceNotFoundException)
            {

            }
            catch (TaskCanceledException)
            {
            }

        }

    }

    #region Room

    private async Task initRooms(Dictionary<string, Guid> connectionPoints, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        roomClient = new NodeRoomClient(
            this,
            LogHandle,
            OnChangeRoomState,
            roomStartInfo,
            connectionPoints,
            udpEndPointConnectionUrl,
            ()=> OnNodeDisconnect(LocalNode.NodeInfo));

        roomClient.OnExecute += roomClient_OnExecute;

        roomClient.OnTransport += roomClient_OnTransport;

        roomClient.OnRoomStartupInfoReceive += startupInfo =>
        {
            NeedWaitAll = bool.Parse(startupInfo["waitAll"]);
            TotalNodeCount = int.Parse(startupInfo["nodeWaitCount"]);
        };

        roomClient.OnChangeNodeList = (roomServer, data, instance) =>
        {
            foreach (var item in data)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var nodeClient = connectedClients.GetOrAdd(item.NodeId, id => new NodeClient(item, this, LogHandle, instance, udpBindingPoint));

                if (!nodeClient.IsLocalNode && nodeClient.State == NodeClientStateEnum.None)
                {
                    if (nodeClient.TryConnect(item))
                        Invoke(() => OnNodeConnect(nodeClient.NodeInfo));
                    else
                        throw new Exception($"Cannot connect");
                }
                else if (nodeClient.IsLocalNode)
                {
                    LocalNode = nodeClient;
                    OnNodeConnect(nodeClient.NodeInfo);
                }
            }

            OnChangeNodesReady(data.Count(), TotalNodeCount);
        };

        await roomClient.Initialize(cancellationToken);
    }

    private void roomClient_OnExecute(InputPacketBuffer buffer)
    {
        Invoke((NodeInfo)null, buffer);
    }

    private void roomClient_OnTransport(Guid nodeId, InputPacketBuffer buffer)
    {
        if (!connectedClients.TryGetValue(nodeId, out var client))
            return;

        Invoke(client.NodeInfo, buffer);
    }

    #endregion

    private async Task waitNodeConnection(CancellationToken cancellationToken)
    {
        OnChangeRoomState(NodeRoomStateEnum.WaitConnections);

        bool valid = false;

        do
        {
            await Task.Delay(100, cancellationToken);

            for (int i = 0; (i < MaxNodesWaitCycle || MaxNodesWaitCycle == 0) && connectedClients.Count < TotalNodeCount - 1; i++)
            {
                await Task.Delay(200, cancellationToken);

                OnChangeNodesReadyDelay(i + 1, MaxNodesWaitCycle);
            }

            if (!valid)
                valid = await roomClient.SendReady(TotalNodeCount, connectedClients.Select(x => x.Key));

        } while (!Ready);
    }

    #region Transport

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Broadcast(DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(packet, packet.Channel, false); });

        if (disposeOnSend)
            packet.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Broadcast(DgramOutputPacketBuffer packet, UDPChannelEnum channel, bool disposeOnSend = true)
    {
        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(packet, channel, false); });

        if (disposeOnSend)
            packet.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Broadcast(ushort code, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(builder, code); });

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Broadcast(ushort code, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(builder, code, channel); });

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Broadcast(Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(builder); });

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Broadcast(UDPChannelEnum channel, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(builder, channel); });

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(Guid nodeId, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node.NodeInfo, packet, disposeOnSend);
        else if (disposeOnSend)
            packet.Dispose();

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(Guid nodeId, UDPChannelEnum channel, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node.NodeInfo, channel, packet, disposeOnSend);
        else if (disposeOnSend)
            packet.Dispose();

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeInfo node, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        node.Network.Send(packet, packet.Channel, disposeOnSend);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeInfo node, UDPChannelEnum channel, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        node.Network.Send(packet, channel, disposeOnSend);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeClient node, ushort code, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        node.Send(code, builder);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeClient node, ushort code, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        node.Send(code, builder, channel);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeClient node, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        node.Send(builder);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeClient node, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        node.Send(builder, channel);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(Guid nodeId, Action<DgramOutputPacketBuffer> builder)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, builder);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(Guid nodeId, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> builder)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, channel, builder);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(Guid nodeId, ushort command, Action<DgramOutputPacketBuffer> build)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, command, build);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(Guid nodeId, ushort command, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> build)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, command, channel, build);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeInfo node, ushort command, Action<DgramOutputPacketBuffer> build)
    {
        node.Network.Send(command, build);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeInfo node, ushort command, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> build)
    {
        node.Network.Send(command, build, channel);

        return true;
    }

    #endregion


    #region SendToServer

    public void SendToServer(ushort command, Action<OutputPacketBuffer> build)
    {
        var packet = new OutputPacketBuffer();

        packet.WriteUInt16(command);

        build(packet);

        packet.WithPid(RoomPacketEnum.Execute);

        SendToServer(packet);
    }

    public void SendToServer(OutputPacketBuffer packet, bool disposeOnSend = true)
    {
        roomClient.SendToServers(packet);

        if (disposeOnSend)
            packet.Dispose();
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
    public void Invoke(NodeInfo nodeInfo, InputPacketBuffer buffer)
    {
        var code = buffer.ReadUInt16();

        var handle = GetHandle(code);

        if (handle == null)
            return;

        Invoke(() => handle(nodeInfo, buffer), buffer);
    }

    public virtual void Invoke(Action action, InputPacketBuffer buffer)
    {
        Invoke(action);
    }

    public virtual void Invoke(Action action)
    {
        action();
    }

    #endregion


    #region IRoomInfo

    public DgramOutputPacketBuffer CreateSendToPacket(ushort command)
    {
        var packet = new DgramOutputPacketBuffer
        {
            PacketId = (ushort)RoomPacketEnum.Transport
        };

        packet.WriteUInt16(command);

        return packet;
    }

    public OutputPacketBuffer CreateSendToServerPacket(ushort command)
    {
        var packet = new OutputPacketBuffer
        {
            PacketId = (ushort)RoomPacketEnum.Execute
        };

        packet.WriteUInt16(command);

        return packet;
    }

    #endregion

    protected virtual void LogHandle(LoggerLevel level, string content)
    {
    }

    public void Dispose()
    {
        if (RoomInfo is IDisposable d)
            d.Dispose();

        roomClient?.Dispose();
        udpBindingPoint?.Stop();

        foreach (var item in connectedClients)
        {
            item.Value.NodeInfo.Network.Dispose();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NodeInfo GetNode(Guid id)
    {
        if (connectedClients.TryGetValue(id, out var node))
            return node.NodeInfo;

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IEnumerable<NodeInfo> GetNodes()
        => connectedClients.Values.Select(x => x.NodeInfo).ToArray();

    public void SendLobbyFinishRoom(byte[] data = null)
    {
        throw new NotImplementedException();
    }

    public void SendLobbyRoomMessage(byte[] data)
    {
        throw new NotImplementedException();
    }

    public void Dispose(byte[] data)
    {
        throw new NotImplementedException();
    }
}