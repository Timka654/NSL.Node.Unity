using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.UDP;
using NSL.UDP.Client;
using NSL.Utils;
using Open.Nat;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking.Types;
using static UnityEditor.ObjectChangeEventStream;

public class NodeNetwork<TRoomInfo> : IRoomInfo, INodeNetwork, IDisposable
    where TRoomInfo : GameInfo
{
    public NodeBridgeClient bridgeClient;

    public NodeRoomClient roomClient;


    public event Action<NodeInfo> OnNodeConnect = node => { };

    public event Action OnRoomReady = () => { };

    public int TotalNodeCount { get; private set; }

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
        RoomInfo = Activator.CreateInstance(typeof(TRoomInfo), this) as TRoomInfo;

        roomStartInfo = startupInfo;

        LocalNodeId = Guid.Parse(roomStartInfo.Token.Split(':').First());

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
        if (state == NodeRoomStateEnum.Ready)
            OnRoomReady();
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

        roomClient.OnRoomStartupInfoReceive += startupInfo =>
        {
            TotalNodeCount = startupInfo.GetRoomNodeCount();
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
                        OnNodeConnect(nodeClient.NodeInfo);
                    else
                        throw new Exception($"Cannot connect");
                }
            }

            OnChangeNodesReady(data.Count(), TotalNodeCount);
        };

        return roomClient.Initialize(cancellationToken);
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

    public void Broadcast(DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(packet, packet.Channel, false); });

        if (disposeOnSend)
            packet.Dispose();
    }

    public bool Broadcast(ushort code, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(builder, code); });

        return true;
    }

    public bool Broadcast(Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        Parallel.ForEach(connectedClients, c => { c.Value.SendBroadcast(builder); });

        return true;
    }

    public bool SendTo(Guid nodeId, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node.NodeInfo, packet, disposeOnSend);
        else if (disposeOnSend)
            packet.Dispose();

        return false;
    }

    public bool SendTo(NodeInfo node, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        node.Network.Send(packet, packet.Channel, disposeOnSend);

        return true;
    }

    public bool SendTo(NodeClient node, Action<DgramOutputPacketBuffer> builder, ushort code)
    {
        if (!Ready)
            return false;

        node.Send(builder, code);

        return true;
    }

    public bool SendTo(NodeClient node, Action<DgramOutputPacketBuffer> builder)
    {
        if (!Ready)
            return false;

        node.Send(builder);

        return true;
    }

    public bool SendTo(Guid nodeId, Action<DgramOutputPacketBuffer> builder)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, builder);

        return false;
    }

    public bool SendTo(Guid nodeId, Action<DgramOutputPacketBuffer> builder, ushort code)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, builder, code);

        return false;
    }

    public bool SendTo(Guid nodeId, ushort command, Action<DgramOutputPacketBuffer> build)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(nodeId, build, command);

        return false;
    }

    public bool SendTo(NodeInfo node, ushort command, Action<DgramOutputPacketBuffer> build)
    {
        node.Network.Send(build, command);

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

        handle(nodeInfo, buffer);
    }

    public virtual void Invoke(Action action, InputPacketBuffer buffer)
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

        bridgeClient?.Dispose();
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
}