using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.SocketCore.Unity;
using NSL.BuilderExtensions.UDPServer;
using NSL.Node.BridgeServer.Shared.Enums;
using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.SocketServer.Utils;
using NSL.UDP.Client;
using NSL.UDP.Client.Info;
using NSL.UDP.Client.Interface;
using NSL.Utils;
using NSL.Utils.Unity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public class NodeNetwork : UnityEngine.MonoBehaviour, IRoomInfo
{
    public NodeBridgeClient bridgeClient;

    public NodeRoomClient transportClient;

    private RoomStartInfo roomStartInfo;

    private UDPServer<UDPNodeServerNetworkClient> endPoint;

    private string endPointConnectionUrl;

    private ConcurrentDictionary<Guid, NodeClient> connectedClients = new ConcurrentDictionary<Guid, NodeClient>();

    private List<StunServerInfo> STUNServers = new List<StunServerInfo>()
    {
        new StunServerInfo("stun.l.google.com:19302"),
        new StunServerInfo("stun1.l.google.com:19302"),
        new StunServerInfo("stun2.l.google.com:19302"),
        new StunServerInfo("stun3.l.google.com:19302"),
        new StunServerInfo("stun4.l.google.com:19302")
    };

    /// <summary>
    /// Can set how transport all data - P2P, Proxy, All
    /// default: All
    /// </summary>
    public NodeTransportMode TransportMode { get; set; } = NodeTransportMode.ProxyOnly;

    /// <summary>
    /// 1 unit = 1 second
    /// for no wait connections set this value to default = 0
    /// </summary>
    public int MaxNodesWaitCycle = 10;

    /// <summary>
    /// Receive transport servers from bridge server delay before continue
    /// </summary>
    public int WaitBridgeDelayMS = 3000;

    public bool DebugPacketIO = true;

    public event OnChangeRoomStateDelegate OnChangeRoomState = state =>
    {
    };
    public event OnChangeNodesReadyDelegate OnChangeNodesReady = (current, total) => { };
    public event OnChangeNodesReadyDelayDelegate OnChangeNodesReadyDelay = (current, total) => { };

    /// <summary>
    /// Id for local enemy
    /// </summary>
    public Guid LocalNodeId { get; private set; } = Guid.Empty;

    public RoomStateEnum CurrentState { get; private set; }

#if DEBUG

    private void DebugOnChangeRoomState(RoomStateEnum state)
    {
        LogHandle(LoggerLevel.Debug, $"{nameof(NodeNetwork)} change state -> {state}");
    }

#endif


    internal async void Initialize(RoomStartInfo startupInfo, CancellationToken cancellationToken = default)
        => await InitializeAsync(startupInfo, cancellationToken);

    public GameInfo GameInfo { get; private set; }

    internal async Task InitializeAsync(RoomStartInfo startupInfo, CancellationToken cancellationToken = default)
    {
#if DEBUG
        OnChangeRoomState -= DebugOnChangeRoomState;
        OnChangeRoomState += DebugOnChangeRoomState;
#endif
        GameInfo = new GameInfo(this);

        roomStartInfo = startupInfo;

        OnChangeRoomState -= OnChangeState;
        OnChangeRoomState += OnChangeState;

        await TryConnectAsync(cancellationToken);

    }

    public bool Ready { get; private set; }

    private void OnChangeState(RoomStateEnum state)
    {
        CurrentState = state;
        Ready = state == RoomStateEnum.Ready;
    }

    private async void TryConnect(CancellationToken cancellationToken = default)
        => await TryConnectAsync(cancellationToken);

    private async Task TryConnectAsync(CancellationToken cancellationToken = default)
    {
        //#if DEBUG
        //        TransportMode = NodeTransportMode.P2POnly;
        //#endif

        try
        {
            OnChangeRoomState(RoomStateEnum.ConnectionBridge);

            bridgeClient = new NodeBridgeClient(this, LogHandle, roomStartInfo.ConnectionEndPoints);

            List<TransportSessionInfoModel> connectionPoints = new List<TransportSessionInfoModel>();

            bridgeClient.OnAvailableBridgeServersResult = (result, instance, from, servers) =>
            {
#if DEBUG
                LogHandle(LoggerLevel.Debug, $"Result {result} from {from}");
#endif

                if (result)
                    connectionPoints.AddRange(servers);
            };

            int serverCount = bridgeClient.Connect(roomStartInfo.ServerIdentity, roomStartInfo.RoomId, roomStartInfo.Token);

            if (serverCount == default)
                throw new Exception($"Can't find working servers");

            await WaitBridgeAsync(connectionPoints, cancellationToken);

            if (TransportMode.HasFlag(NodeTransportMode.P2POnly))
            {
                //throw new Exception("Commented code");
                CreateUdpEndPoint();
            }


            InitializeTransportClients(connectionPoints, cancellationToken);

            await WaitNodeConnection(cancellationToken);

        }
        catch (TaskCanceledException)
        {
            // todo: dispose all
            throw;
        }
    }

    private async Task WaitBridgeAsync(List<TransportSessionInfoModel> connectionPoints, CancellationToken cancellationToken = default)
    {
        OnChangeRoomState(RoomStateEnum.WaitTransportServerList);

#if DEBUG
        WaitBridgeDelayMS = 10_000;
#endif

        await Task.Delay(WaitBridgeDelayMS, cancellationToken);

        if (!connectionPoints.Any())
            throw new Exception($"WaitAndRun : Can't find any working servers");
    }

    private void CreateUdpEndPoint()
    {
        endPoint = UDPServerEndPointBuilder
            .Create()
            .WithClientProcessor<UDPNodeServerNetworkClient>()
            .WithOptions<UDPServerOptions<UDPNodeServerNetworkClient>>()
            .WithBindingPoint(new IPEndPoint(IPAddress.Any, 0))
            .WithCode(builder =>
            {
                var options = builder.GetOptions() as ISTUNOptions;

                options.StunServers.AddRange(STUNServers);

                builder.AddExceptionHandle((ex, c) =>
                {
                    LogHandle(LoggerLevel.Error, ex.ToString());
                });

                //builder.AddReceivePacketHandle(NodeTransportPacketEnum.Transport,)
            })
            .Build();

        endPoint.Start();

        if (endPoint?.StunInformation != null)
            endPointConnectionUrl = NSLEndPoint.FromIPAddress(
                NSLEndPoint.Type.UDP,
                endPoint.StunInformation.PublicEndPoint.Address,
                endPoint.StunInformation.PublicEndPoint.Port
                ).ToString();
        else
            endPointConnectionUrl = default;
    }

    private void InitializeTransportClients(List<TransportSessionInfoModel> connectionPoints, CancellationToken cancellationToken = default)
    {
        OnChangeRoomState(RoomStateEnum.ConnectionTransportServers);

        var point = connectionPoints.First();

        transportClient = new NodeRoomClient(this, LogHandle, point.ConnectionUrl);

        transportClient.OnSignOnServerResult = (result, instance, url) =>
        {
            if (result)
                return;

            LogHandle(LoggerLevel.Error, $"Cannot sign on {nameof(NodeRoomClient)}");
        };

        transportClient.OnRoomReady += (createTime, srv_offs) =>
        {
#if DEBUG
            LogHandle(LoggerLevel.Debug, $"{nameof(transportClient.OnRoomReady)} - {createTime} - {srv_offs}");
#endif
            OnChangeRoomState(RoomStateEnum.Ready);
        };

        transportClient.OnExecute += TransportClient_OnExecute;


        transportClient.OnChangeNodeList = (data, instance) =>
        {
            foreach (var item in data)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (item.NodeId == LocalNodeId)
                    continue;

                var nodeClient = connectedClients.GetOrAdd(item.NodeId, id => new NodeClient(item, this, instance));

                if (nodeClient.State == NodeClientState.None)
                {

                    if (!nodeClient.TryConnect(item))
                        throw new Exception($"Cannot connect");
                }
            }

            OnChangeNodesReady(data.Count(), roomStartInfo.TotalPlayerCount);
        };

        if (cancellationToken.IsCancellationRequested)
            throw new TaskCanceledException();

        if (transportClient.Connect(LocalNodeId = point.Id, roomStartInfo.Token, endPointConnectionUrl) == default)
            throw new Exception($"WaitAndRun : Can't find working transport servers");
    }

    private void TransportClient_OnExecute(InputPacketBuffer buffer)
    {
        var handle = GetHandle(buffer.ReadUInt16());

        if (handle == null)
            return;

        buffer.ManualDisposing = true;

        ThreadHelper.InvokeOnMain(() =>
        {
            handle(null, buffer);

            buffer.Dispose();
        });
    }

    private async Task WaitNodeConnection(CancellationToken cancellationToken = default)
    {
        OnChangeRoomState(RoomStateEnum.WaitConnections);

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
                valid = await transportClient.SendReady(roomStartInfo.TotalPlayerCount, connectedClients.Select(x => x.Key).Append(LocalNodeId));

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
        transportClient.SendToServers(packet);
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

    private void LogHandle(LoggerLevel level, string content)
    {
        switch (level)
        {
            case LoggerLevel.Error:
                UnityEngine.Debug.LogError(content);
                break;
            case LoggerLevel.Info:
            case LoggerLevel.Log:
            case LoggerLevel.Debug:
            case LoggerLevel.Performance:
            default:
                UnityEngine.Debug.Log(content);
                break;
        }
    }
}


public class UDPNodeServerNetworkClient : IServerNetworkClient
{

}

public delegate void OnChangeRoomStateDelegate(RoomStateEnum state);
public delegate void OnChangeNodesReadyDelegate(int current, int total);
public delegate void OnChangeNodesReadyDelayDelegate(int current, int total);

public enum RoomStateEnum
{
    ConnectionBridge,
    WaitTransportServerList,
    ConnectionTransportServers,
    WaitConnections,
    Ready
}

[Flags]
public enum NodeTransportMode
{
    P2POnly = 1,
    ProxyOnly = 2,
    All = P2POnly | ProxyOnly
}

public delegate void NodeLogDelegate(LoggerLevel level, string content);