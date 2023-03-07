using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.WebSocketsClient;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Extensions.Buffer;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.WebSockets.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

public class NodeRoomClient : IDisposable
{
    private readonly INodeNetworkOptions node;
    private readonly NodeLogDelegate logHandle;
    private readonly OnChangeRoomStateDelegate changeStateHandle;
    private readonly NodeSessionStartupModel roomStartInfo;
    private readonly IEnumerable<RoomSessionInfoModel> connectionPoints;
    private readonly string localNodeUdpEndPoint;

    public int ConnectionTimeout { get; set; } = 10_000;

    private Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>> connections = new Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>>();

    public NodeRoomClient(
        INodeNetworkOptions node, 
        NodeLogDelegate logHandle,
        OnChangeRoomStateDelegate changeStateHandle,
        NodeSessionStartupModel roomStartInfo,
        IEnumerable<RoomSessionInfoModel> connectionPoints,
        string localNodeUdpEndPoint)
    {
        this.node = node;
        this.logHandle = logHandle;
        this.changeStateHandle = changeStateHandle;
        this.roomStartInfo = roomStartInfo;
        this.connectionPoints = connectionPoints;
        this.localNodeUdpEndPoint = localNodeUdpEndPoint;
    }

    public Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        changeStateHandle(NodeRoomStateEnum.ConnectionTransportServers);

        if (connectToServers(cancellationToken) == default)
            throw new Exception($"WaitAndRun : Can't find working transport servers");

        return Task.CompletedTask;
    }

    private async Task<int> connectToServers(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dispose();

        cancellationToken.ThrowIfCancellationRequested();

        var roomServers = connectionPoints.ToDictionary(
            point => new Uri(point.ConnectionUrl),
            point => WebSocketsClientEndPointBuilder.Create()
                .WithClientProcessor<RoomNetworkClient>()
                .WithOptions<WSClientOptions<RoomNetworkClient>>()
                .WithCode(builder =>
                {
                    builder.AddConnectHandle(client =>
                    {
                        client.ServerUrl = new Uri(point.ConnectionUrl);
                        client.SessionInfo = point;

                        client.PingPongEnabled = true;

                        var packet = OutputPacketBuffer.Create(RoomPacketEnum.SignSession);

                        packet.WriteString16(roomStartInfo.Token);
                        packet.WriteGuid(point.Id);
                        packet.WriteString16(localNodeUdpEndPoint);

                        client.Network.Send(packet);
                    });

                    if (node.DebugPacketIO)
                    {
                        builder.AddSendHandle((c, pid, len, st) =>
                        {
                            logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Send {pid}");
                        });

                        builder.AddReceiveHandle((c, pid, len) =>
                        {
                            logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Receive {pid}");
                        });
                    }

                    builder.AddPacketHandle(RoomPacketEnum.SignSessionResult, OnSignSessionReceive);
                    builder.AddPacketHandle(RoomPacketEnum.ChangeNodeList, OnChangeNodeListReceive);
                    builder.AddPacketHandle(RoomPacketEnum.Transport, OnTransportReceive);
                    builder.AddPacketHandle(RoomPacketEnum.Execute, OnExecuteReceive);
                    builder.AddReceivePacketHandle(RoomPacketEnum.ReadyNodeResult, c => c.PacketWaitBuffer);
                    builder.AddPacketHandle(RoomPacketEnum.ReadyRoom, OnRoomReadyReceive);
                    builder.AddPacketHandle(RoomPacketEnum.StartupInfoMessage, OnStartupInfoReceive);
                })
                .WithUrl(new Uri(point.ConnectionUrl))
                .Build());

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in roomServers)
        {
            if (!await item.Value.ConnectAsync(ConnectionTimeout))
            {
                logHandle?.Invoke(LoggerLevel.Info, $"Cannot connect to {item.Key}");
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();

            connections.Add(item.Key, item.Value);
        }

        return connections.Count;
    }

    #region Send

    public async Task<bool> SendReady(int totalCount, IEnumerable<Guid> readyNodes)
    {
        var p = WaitablePacketBuffer.Create(RoomPacketEnum.ReadyNode);

        p.WriteInt32(totalCount);
        p.WriteCollection(readyNodes, i => p.WriteGuid(i));

        bool state = false;

        foreach (var item in connections)
        {
            await item.Value.Data.PacketWaitBuffer.SendWaitRequest(p, data =>
            {
                state = data.ReadBool();

                return Task.CompletedTask;
            });

            if (!state)
                return state;
        }

        return state;
    }

    public void SendToServers(OutputPacketBuffer packet)
    {
        foreach (var item in connections)
        {
            if (!item.Value.GetState())
                continue;

            item.Value.Send(packet, false);
        }
    }

    #endregion

    #region ReceiveHandles

    private void OnStartupInfoReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnRoomStartupInfoReceive(new NSL.Node.BridgeServer.Shared.NodeRoomStartupInfo(data.ReadCollection(p => new KeyValuePair<string,string>(p.ReadString16(), p.ReadString16()))));
    }

    private void OnSignSessionReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        var result = data.ReadBool();

        if (result)
            client.RequestServerTimeOffset();
        else
        {
            logHandle(LoggerLevel.Error, $"Cannot sign on {nameof(NodeRoomClient)}");
        }
    }

    private void OnChangeNodeListReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnChangeNodeList(client, data.ReadCollection(() => new NodeConnectionInfoModel(data.ReadGuid(), data.ReadString16(), data.ReadString16())), this);
    }

    private void OnRoomReadyReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        var offset = client.ServerDateTimeOffset;

        if ((offset < TimeSpan.Zero && offset > TimeSpan.FromSeconds(-1)) || (offset > TimeSpan.Zero && offset < TimeSpan.FromSeconds(1)))
            offset = TimeSpan.Zero;

        var createTime = data.ReadDateTime();

#if DEBUG
        logHandle(LoggerLevel.Debug, $"{nameof(OnRoomReadyReceive)} - {createTime} - {offset}");
#endif

        changeStateHandle(NodeRoomStateEnum.Ready);
    }

    private void OnTransportReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        var nid = data.ReadGuid();

        var len = (int)(data.Lenght - data.Position);

        var packet = new InputPacketBuffer(data.Read(len));

        OnTransport(nid, packet);
    }

    private void OnExecuteReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnExecute(data);
    }

    #endregion

    public OnNodeRoomReceiveNodeListDelegate OnChangeNodeList = (roomServer, data, transportClient) => { };

    public event OnNodeRoomTransportDelegate OnTransport = (nodeId, buffer) => { };

    public event OnNodeRoomExecuteDelegate OnExecute = (buffer) => { };

    public event OnRoomStartupInfoReceiveDelegate OnRoomStartupInfoReceive = (startupInfo) => { };

    public void Dispose()
    {
        foreach (var item in connections)
        {
            if (item.Value.GetState())
                item.Value.Disconnect();
        }

        connections.Clear();
    }
}
