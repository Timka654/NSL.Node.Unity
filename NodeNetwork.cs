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
using UnityEngine;
using static NSL.Node.RoomServer.Shared.Client.Core.IRoomInfo;

public class NodeNetwork : IRoomInfo, INodeNetwork, IDisposable
{
    public NodeRoomClient roomClient;

    public event OnNodeDelegate OnNodeConnect = node => Task.CompletedTask;

    public event OnNodeDelegate OnNodeConnectionLost = node => Task.CompletedTask;

    public event IRoomInfo.OnNodeDisconnectDelegate OnNodeDisconnect = (node, manualDisconnected) => Task.CompletedTask;

    public event Func<Task> OnRoomReady = () => Task.CompletedTask;

    public int TotalNodeCount { get; private set; }

    private bool NeedWaitAll { get; set; }

    private NodeSessionStartupModel roomStartInfo;

    private UDPServer<UDPNodeServerNetworkClient> udpBindingPoint;

    private string udpEndPointConnectionUrl;

    public NSLSessionInfo Session { get; private set; }

    private ConcurrentDictionary<string, NodeClient> connectedClients = new ConcurrentDictionary<string, NodeClient>();

    /// <summary>
    /// Can set how transport all data - P2P, Proxy, All
    /// default: All
    /// </summary>
    public NodeTransportModeEnum TransportMode { get; set; } = NodeTransportModeEnum.ProxyOnly;

    /// <summary>
    /// default = 30 000 ms
    /// </summary>
    public int MaxReadyWaitDelay { get; set; } = 30000;

    public bool DebugPacketIO { get; set; } = true;

    public NodeNetworkChannelType NetworkChannelType { get; set; } = NodeNetworkChannelType.TCP;

    public event OnChangeRoomStateDelegate OnChangeRoomState = state =>
    {
    };

    public event OnChangeNodesReadyDelegate OnChangeNodesReady = (current, total) => { };
    public event OnNodeDelegate OnRecoverySession = node => Task.CompletedTask;

    public NodeRoomStateEnum CurrentState { get; private set; }

    public bool Ready { get; private set; }

    public IRoomSession RoomSession { get; private set; }

    public string LocalNodeId { get; private set; }

    public NodeClient LocalNode { get; private set; }

    private CancellationTokenSource readyWaitToken;


    internal async void Initialize(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
        => await InitializeAsync(startupInfo, cancellationToken);

    internal async Task InitializeAsync(NodeSessionStartupModel startupInfo, CancellationToken cancellationToken = default)
    {
        roomStartInfo = startupInfo;

        LocalNodeId = roomStartInfo.Token.Split(':').First();

        readyWaitToken = new CancellationTokenSource();

        //Debug.LogError($"{nameof(InitializeAsync)} - {LocalNodeId} - {string.Join(",", startupInfo.ConnectionEndPoints.Select(x=>$"[{x.Key} - {x.Value}]").ToArray())}");

        try
        {
            await initUDPBindingPoint(cancellationToken);

            await initRooms(startupInfo.ConnectionEndPoints, cancellationToken);

            if (!readyWaitToken.Token.IsCancellationRequested)
                await DelayHandle(MaxReadyWaitDelay, readyWaitToken.Token, false);

            if (CurrentState != NodeRoomStateEnum.Ready)
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
        LogHandle(LoggerLevel.Debug, $"{nameof(NodeNetwork)}({roomStartInfo}) change state -> {state}");
#endif

        CurrentState = state;

        Ready = state == NodeRoomStateEnum.Ready;

        OnChangeRoomState(state);

        if (state == NodeRoomStateEnum.Invalid)
        {
            LogHandle(LoggerLevel.Error, $"Set invalid state for {roomStartInfo}");
        }

        if (state == NodeRoomStateEnum.Ready)
        {
            readyWaitToken.Cancel();
            Invoke(() => OnRoomReady());
        }
    }

    #region Room

    private async Task initRooms(Dictionary<string, Guid> connectionPoints, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ChangeState(NodeRoomStateEnum.ConnectionTransportServers);

        if (connectionPoints.All(x => x.Key.StartsWith("ws")))
            NetworkChannelType = NodeNetworkChannelType.WS;
        else if (connectionPoints.All(x => x.Key.StartsWith("tcp")))
            NetworkChannelType = NodeNetworkChannelType.TCP;

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

    private void roomClient_OnTransport(string nodeId, InputPacketBuffer buffer)
    {
        if (!connectedClients.TryGetValue(nodeId, out var client))
            return;

        Invoke(client.NodeInfo, buffer);
    }

    #endregion

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
    public bool SendTo(string nodeId, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node.NodeInfo, packet, disposeOnSend);
        else if (disposeOnSend)
            packet.Dispose();

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(string nodeId, UDPChannelEnum channel, DgramOutputPacketBuffer packet, bool disposeOnSend = true)
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
    public bool SendTo(NodeInfo node, byte[] buffer)
    {
        //node.Network.Send(buffer);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(NodeInfo node, byte[] buffer, int offset, int len)
    {
        return false;
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
    public bool SendTo(string nodeId, Action<DgramOutputPacketBuffer> builder)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, builder);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(string nodeId, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> builder)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, channel, builder);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(string nodeId, ushort command, Action<DgramOutputPacketBuffer> build)
    {
        if (connectedClients.TryGetValue(nodeId, out var node))
            return SendTo(node, command, build);

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendTo(string nodeId, ushort command, UDPChannelEnum channel, Action<DgramOutputPacketBuffer> build)
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
        var rc = roomClient;
        rc?.SendToServers(packet);

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
        LogHandle(LoggerLevel.Info, $"Dispose {roomStartInfo}");
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
    public NodeInfo GetNode(string id)
    {
        if (connectedClients.TryGetValue(id, out var node))
            return node.NodeInfo;

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IEnumerable<NodeInfo> GetNodes()
        => connectedClients.Values.Select(x => x.NodeInfo).ToArray();

    public Task RecoverySession(NodeInfo node)
    {
        throw new NotImplementedException();
    }
}

public enum NodeNetworkChannelType
{
    WS,
    TCP
}