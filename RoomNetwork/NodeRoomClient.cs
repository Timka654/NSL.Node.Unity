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
using System.Threading;
using System.Threading.Tasks;

public class NodeRoomClient : IDisposable
{
    private readonly INodeNetworkOptions node;
    private readonly NodeLogDelegate logHandle;
    private readonly OnChangeRoomStateDelegate changeStateHandle;
    private readonly NodeSessionStartupModel roomStartInfo;
    private readonly Dictionary<string, Guid> connectionPoints;
    private readonly string localNodeUdpEndPoint;
    private readonly Action onDisconnect;

    public int ConnectionTimeout { get; set; } = 10_000;

    private Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>> connections = new Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>>();

    public NodeRoomClient(
        INodeNetworkOptions node,
        NodeLogDelegate logHandle,
        OnChangeRoomStateDelegate changeStateHandle,
        NodeSessionStartupModel roomStartInfo,
        Dictionary<string, Guid> connectionPoints,
        string localNodeUdpEndPoint,
        Action onDisconnect)
    {
        this.node = node;
        this.logHandle = logHandle;
        this.changeStateHandle = changeStateHandle;
        this.roomStartInfo = roomStartInfo;
        this.connectionPoints = connectionPoints;
        this.localNodeUdpEndPoint = localNodeUdpEndPoint;
        this.onDisconnect = onDisconnect;
    }

    public async Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        changeStateHandle(NodeRoomStateEnum.ConnectionTransportServers);

        if (await connectToServers(cancellationToken) == default)
            throw new Exception($"WaitAndRun : Can't find working transport servers");

    }

    private async Task<int> connectToServers(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dispose();

        cancellationToken.ThrowIfCancellationRequested();

        var roomServers = connectionPoints.ToDictionary(
            point => new Uri(point.Key),
            point => WebSocketsClientEndPointBuilder.Create()
                .WithClientProcessor<RoomNetworkClient>()
                .WithOptions<WSClientOptions<RoomNetworkClient>>()
                .WithCode(builder =>
                {
                    builder.AddConnectHandle(client =>
                    {
                        client.ServerUrl = new Uri(point.Key);

                        client.PingPongEnabled = true;

                        var packet = OutputPacketBuffer.Create(RoomPacketEnum.SignSession);

                        packet.WriteGuid(point.Value);

                        packet.WriteGuid(roomStartInfo.RoomId);

                        packet.WriteString(roomStartInfo.Token);

                        packet.WriteString(localNodeUdpEndPoint);

                        client.Network.Send(packet);
                    });

                    builder.AddDisconnectHandle(client => {
                        onDisconnect();
                    });

                    if (node.DebugPacketIO)
                    {
                        builder.AddSendHandle((c, pid, len, st) =>
                        {
                            if (pid < ushort.MaxValue - 100)
                                logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Send {pid}");
                        });

                        builder.AddReceiveHandle((c, pid, len) =>
                        {
                            if (pid < ushort.MaxValue - 100)
                                logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Receive {pid}");
                        });
                    }

                    builder.AddPacketHandle(RoomPacketEnum.SignSessionResult, OnSignSessionReceive);
                    builder.AddPacketHandle(RoomPacketEnum.ChangeNodeList, OnChangeNodeListReceive);
                    builder.AddPacketHandle(RoomPacketEnum.Transport, OnTransportReceive);
                    builder.AddPacketHandle(RoomPacketEnum.Execute, OnExecuteReceive);
                    builder.AddReceivePacketHandle(RoomPacketEnum.Response, c => c.PacketWaitBuffer);
                    builder.AddPacketHandle(RoomPacketEnum.ReadyRoom, OnRoomReadyReceive);
                    builder.AddPacketHandle(RoomPacketEnum.StartupInfoMessage, OnStartupInfoReceive);
                })
                .WithUrl(new Uri(point.Key))
                .Build());

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in roomServers)
        {
            logHandle?.Invoke(LoggerLevel.Info, $"Try connect to {item.Key}");
            if (!await Task.Run(() => item.Value.Connect(ConnectionTimeout)))
            {
                logHandle?.Invoke(LoggerLevel.Info, $"Cannot connect to {item.Key}");
                continue;
            }

            logHandle?.Invoke(LoggerLevel.Info, $"Success connect to {item.Key}");
            cancellationToken.ThrowIfCancellationRequested();

            connections.Add(item.Key, item.Value);
        }

        return connections.Count;
    }

    #region Send

    public async Task<bool> SendReady(int totalCount, IEnumerable<Guid> readyNodes)
    {
        var p = RequestPacketBuffer.Create(RoomPacketEnum.ReadyNodeRequest);

        p.WriteInt32(totalCount);
        p.WriteCollection(readyNodes, i => p.WriteGuid(i));

        bool state = false;

        foreach (var item in connections)
        {
            await item.Value.Data.PacketWaitBuffer.SendRequestAsync(p, data =>
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

            ((OutputPacketBuffer)packet).Send(item.Value, false);
        }
    }

    #endregion

    #region ReceiveHandles

    private void OnStartupInfoReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnRoomStartupInfoReceive(data.ReadCollection(p => (p.ReadString(), p.ReadString())).ToDictionary(x=>x.Item1, x=>x.Item2));
    }

    private void OnSignSessionReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        var result = data.ReadBool();

        if (result)
        {
            client.RequestServerTimeOffset();

            client.PlayerId = data.ReadGuid();
        }
        else
        {
            logHandle(LoggerLevel.Error, $"Cannot sign on {nameof(NodeRoomClient)}");
        }
    }

    private void OnChangeNodeListReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnChangeNodeList(client, data.ReadCollection(() => new NodeConnectionInfoModel(data.ReadGuid(), data.ReadString(), data.ReadString())), this);
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
        data.ReadGuid(); // local node

        var nid = data.ReadGuid(); // from node

        var len = (int)(data.Length - data.Position);

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
