using Cysharp.Threading.Tasks;
using NSL.Extensions.Session;
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

public class NodeNetwork : IRoomInfo, INodeNetwork, IDisposable
{
    public NodeRoomClient roomClient;

    public event Action<NodeInfo> OnNodeConnect = node => { };

    public event Action<NodeInfo> OnNodeConnectionLost = node => { };

    public event IRoomInfo.OnNodeDisconnectDelegate OnNodeDisconnect = (node, manualDisconnected) => { };

    public event Action OnRoomReady = () => { };

    public int TotalNodeCount { get; private set; }

    private bool NeedWaitAll { get; set; }

    private NodeSessionStartupModel roomStartInfo;

    private UDPServer<UDPNodeServerNetworkClient> udpBindingPoint;

    private string udpEndPointConnectionUrl;

    public NSLSessionInfo Session { get; private set; }

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

    public NodeNetworkChannelType NetworkChannelType { get; set; } = NodeNetworkChannelType.TCP;

    public event OnChangeRoomStateDelegate OnChangeRoomState = state =>
    {
    };

    public event OnChangeNodesReadyDelegate OnChangeNodesReady = (current, total) => { };
    public event Action<NodeInfo> OnRecoverySession = node=> { };

    public NodeRoomStateEnum CurrentState { get; private set; }

    public bool Ready { get; private set; }

    public IRoomSession RoomSession { get; private set; }

    public Guid LocalNodeId { get; private set; }

    public NodeClient LocalNode { get; private set; }


    internal async void Initialize(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
        => await InitializeAsync(startupInfo, cancellationToken);

    internal async Task InitializeAsync(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
    {
        roomStartInfo = startupInfo;

        LocalNodeId = Guid.Parse(roomStartInfo.Token.Split(':').First());

        try
        {
            await initUDPBindingPoint(cancellationToken);

            await initRooms(startupInfo.ConnectionEndPoints, cancellationToken);

            if (!await waitNodeConnection(cancellationToken))
                ChangeState(NodeRoomStateEnum.Invalid);
        }
        catch (TaskCanceledException)
        {
            Dispose();
        }
    }

    /// <summary>
    /// Set session for dispose handle on dispose network example
    /// </summary>
    /// <param name="roomSession"></param>
    /// <exception cref="Exception"></exception>
    public void SetSession(IRoomSession roomSession)
    {
        if (CurrentState != NodeRoomStateEnum.None)
            throw new Exception($"Cannot set room session on {CurrentState}. Need to set this before initialize");

        this.RoomSession = roomSession;
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

    private void ChangeState(NodeRoomStateEnum state)
    {
#if DEBUG
        LogHandle(LoggerLevel.Debug, $"{nameof(NodeNetwork)} change state -> {state}");
#endif

        CurrentState = state;

        Ready = state == NodeRoomStateEnum.Ready;

        OnChangeRoomState(state);

        if (state == NodeRoomStateEnum.Ready)
            Invoke(() => OnRoomReady());
    }

    #region Room

    private async Task initRooms(Dictionary<string, Guid> connectionPoints, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ChangeState(NodeRoomStateEnum.ConnectionTransportServers);




        roomClient = NetworkChannelType switch
        {
            NodeNetworkChannelType.WS => new NodeWSRoomClient(
                                                               this,
                                                               LogHandle,
                                                               ChangeState,
                                                               roomStartInfo,
                                                               connectionPoints,
                                                               udpEndPointConnectionUrl,
                                                               DelayHandle,
                                                               () => OnNodeDisconnect(LocalNode?.NodeInfo, false),
                                                               () => OnRecoverySession(LocalNode?.NodeInfo)),
            NodeNetworkChannelType.TCP => new NodeTCPRoomClient(
                                                               this,
                                                               LogHandle,
                                                               ChangeState,
                                                               roomStartInfo,
                                                               connectionPoints,
                                                               udpEndPointConnectionUrl,
                                                               DelayHandle,
                                                               () => OnNodeDisconnect(LocalNode?.NodeInfo, false),
                                                               () => OnRecoverySession(LocalNode?.NodeInfo)),
        };

        roomClient.OnExecute += roomClient_OnExecute;

        roomClient.OnTransport += roomClient_OnTransport;

        roomClient.OnSignIn += (room, signInfo) =>
        {
            if (!signInfo.Success)
                return;

            NeedWaitAll = bool.Parse(signInfo.Options["waitAll"]);
            TotalNodeCount = int.Parse(signInfo.Options["nodeWaitCount"]);
        };

        roomClient.OnNodeConnect += (instance, item) =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var nodeClient = connectedClients.GetOrAdd(item.NodeId, id => new NodeClient(item, this, LogHandle, instance, udpBindingPoint));

            if (nodeClient.IsLocalNode)
            {
                LocalNode = nodeClient;

                Invoke(() => OnNodeConnect(nodeClient.NodeInfo));
            }
            else if (nodeClient.State == NodeClientStateEnum.None)
            {
                if (nodeClient.TryConnect(item))
                    Invoke(() => OnNodeConnect(nodeClient.NodeInfo));
                else
                    throw new Exception($"Cannot connect");
            }

            OnChangeNodesReady(connectedClients.Count(), TotalNodeCount);
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

    private async Task<bool> waitNodeConnection(CancellationToken cancellationToken)
    {
        if (roomClient == null)
            return false;

        ChangeState(NodeRoomStateEnum.WaitConnections);

        bool valid = false;
        int i = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            valid = await roomClient?.SendReady(TotalNodeCount, connectedClients.Select(x => x.Key)) == true;

            if (valid == false)
                await DelayHandle(2_000, cancellationToken: cancellationToken);

        } while (!valid && roomClient != null && roomClient.AnyServers() && ++i < 10); // i for prevent locking

        return roomClient?.AnySignedServers() == true;
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

        packet.WithPid(RoomPacketEnum.ExecuteMessage);

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
            PacketId = (ushort)RoomPacketEnum.TransportMessage
        };

        packet.WriteUInt16(command);

        return packet;
    }

    public OutputPacketBuffer CreateSendToServerPacket(ushort command)
    {
        var packet = new OutputPacketBuffer
        {
            PacketId = (ushort)RoomPacketEnum.ExecuteMessage
        };

        packet.WriteUInt16(command);

        return packet;
    }

    #endregion

    protected virtual void LogHandle(LoggerLevel level, string content)
    {
    }

    /// <summary>
    /// Delay request handle for implement delay on platforms unsupported <see cref="Task.Delay(int)"/>(and overloads)
    /// </summary>
    /// <param name="milliseconds"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual async Task DelayHandle(int milliseconds, CancellationToken cancellationToken, bool _throw = true)
    {
        try { await Task.Delay(2_000, cancellationToken: cancellationToken); }
        catch (OperationCanceledException)
        {
            if (_throw)
                throw;
        }
    }

    public void Dispose()
    {
        if (RoomSession != null && RoomSession is IDisposable d)
            d.Dispose();

        roomClient?.Dispose();
        udpBindingPoint?.Stop();

        roomClient = null;

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

    public void RecoverySession(NodeInfo node)
    {
        throw new NotImplementedException();
    }
}

public enum NodeNetworkChannelType
{
    WS,
    TCP
}