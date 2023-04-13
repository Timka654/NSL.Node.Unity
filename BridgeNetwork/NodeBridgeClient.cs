using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.WebSocketsClient;
using NSL.Node.BridgeServer.Shared.Enums;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.WebSockets.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class NodeBridgeClient : IDisposable
{
    private readonly IEnumerable<Uri> wssUrls;
    private readonly INodeNetworkOptions node;
    private readonly NodeLogDelegate logHandle;
    private readonly OnChangeRoomStateDelegate changeStateHandle;
    private readonly NodeSessionStartupModel roomStartInfo;

    public int NeedBridgeConnectionsCount { get; set; } = 1;

    public int ConnectionTimeout { get; set; } = 10_000;

    public IEnumerable<RoomSessionInfoModel> RoomServerEndPoints => roomServerEndPoints;

    private Dictionary<Uri, WSNetworkClient<BridgeNetworkClient, WSClientOptions<BridgeNetworkClient>>> connections = new Dictionary<Uri, WSNetworkClient<BridgeNetworkClient, WSClientOptions<BridgeNetworkClient>>>();

    public NodeBridgeClient(
        INodeNetworkOptions node,
        NodeLogDelegate logHandle,
        OnChangeRoomStateDelegate changeStateHandle,
        NodeSessionStartupModel roomStartInfo)
    {
        this.node = node;
        this.logHandle = logHandle;
        this.changeStateHandle = changeStateHandle;
        this.roomStartInfo = roomStartInfo;
        this.wssUrls = roomStartInfo.ConnectionEndPoints.Select(x => new Uri(x)).ToArray();
    }

    public async Task<IEnumerable<RoomSessionInfoModel>> Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        changeStateHandle(NodeRoomStateEnum.ConnectionBridge);

        roomServerEndPoints.Clear();

        using SemaphoreSlim waitBridges = new SemaphoreSlim(0);

        onSignSessionReceive = (result, instance, from, servers) =>
        {
#if DEBUG
            logHandle(LoggerLevel.Debug, $"Result {result} from {from}");
#endif

            if (result)
                roomServerEndPoints.AddRange(servers);

            waitBridges.Release(1);
        };

        int serverCount = connectToServers(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (serverCount == default)
            throw new Exception($"Can't find working bridge servers");

        await waitBridgeAsync(waitBridges, cancellationToken);

        return RoomServerEndPoints;
    }

    private async Task waitBridgeAsync(SemaphoreSlim waitBridges, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        changeStateHandle(NodeRoomStateEnum.WaitTransportServerList);

        var bridgeServerCount = wssUrls.Count();

        int processed = 0;

        while (bridgeServerCount > processed)
        {
            if (await waitBridges.WaitAsync(node.WaitBridgeDelayMS, cancellationToken))
            {
                ++processed;
            }
            else
                --bridgeServerCount;
        }

        if (bridgeServerCount == 0)
            logHandle(LoggerLevel.Error, $"timeout on wait bridge servers");

#if DEBUG
        logHandle(LoggerLevel.Debug, $"Bridge received {processed} from {bridgeServerCount}");
#endif

        cancellationToken.ThrowIfCancellationRequested();

        if (!roomServerEndPoints.Any())
            throw new Exception($"WaitAndRun : Can't find any working servers");
    }

    private int connectToServers(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dispose();

        cancellationToken.ThrowIfCancellationRequested();

        var bridgeServers = wssUrls.ToDictionary(
            uri => uri,
            uri => WebSocketsClientEndPointBuilder.Create()
                .WithClientProcessor<BridgeNetworkClient>()
                .WithOptions<WSClientOptions<BridgeNetworkClient>>()
                .WithCode(builder =>
                {

                    if (node.DebugPacketIO)
                    {
                        builder.AddSendHandle((c, pid, len, st) =>
                        {
                            if (pid < ushort.MaxValue - 100)
                                logHandle?.Invoke(LoggerLevel.Info, $"[Bridge Server] Send {pid}");
                        });

                        builder.AddReceiveHandle((c, pid, len) =>
                        {
                            if (pid < ushort.MaxValue - 100)
                                logHandle?.Invoke(LoggerLevel.Info, $"[Bridge Server] Receive {pid}");
                        });
                    }

                    builder.AddConnectHandle(client => client.Url = uri);
                    builder.AddPacketHandle(NodeBridgeClientPacketEnum.SignSessionResultPID, OnSignSessionReceive);
                })
                .WithUrl(uri)
                .Build());


        var packet = OutputPacketBuffer.Create(NodeBridgeClientPacketEnum.SignSessionPID);

        packet.WriteString16(roomStartInfo.ServerIdentity);
        packet.WriteGuid(roomStartInfo.RoomId);
        packet.WriteString16(roomStartInfo.Token);

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in bridgeServers)
        {
            if (!item.Value.Connect(ConnectionTimeout))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            connections.Add(item.Key, item.Value);

            item.Value.Send(packet, false);

            if (connections.Count == NeedBridgeConnectionsCount)
                break;
        }

        packet.Dispose();

        return connections.Count;
    }

    private void OnSignSessionReceive(BridgeNetworkClient client, InputPacketBuffer data)
    {
        var result = data.ReadBool();

        var sessions = result ? data.ReadCollection(() => RoomSessionInfoModel.Read(data)) : null;

        onSignSessionReceive(result, this, client.Url, sessions);
    }

    public void Dispose()
    {
        foreach (var item in connections)
        {
            if (item.Value.GetState())
                item.Value.Disconnect();
        }

        connections.Clear();
    }

    private OnBridgeReceiveSignSessionResultDelegate onSignSessionReceive = (result, instance, from, servers) => { };

    private List<RoomSessionInfoModel> roomServerEndPoints = new List<RoomSessionInfoModel>();

    private delegate void OnBridgeReceiveSignSessionResultDelegate(bool result, NodeBridgeClient instance, Uri from, IEnumerable<RoomSessionInfoModel> servers);
}
