using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.SocketCore.Unity;
using NSL.BuilderExtensions.WebSocketsClient;
using NSL.Node.BridgeServer.Shared.Enums;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.WebSockets.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.GraphView;

public partial class NodeBridgeClient : IDisposable
{
    public delegate void OnReceiveSignSessionResultDelegate(bool result, NodeBridgeClient instance, Uri from, IEnumerable<TransportSessionInfoModel> servers);

    private readonly IEnumerable<Uri> wssUrls;

    private readonly NodeNetwork node;
    private readonly NodeLogDelegate logHandle;

    private Dictionary<Uri, WSNetworkClient<BridgeNetworkClient, WSClientOptions<BridgeNetworkClient>>> connections = new Dictionary<Uri, WSNetworkClient<BridgeNetworkClient, WSClientOptions<BridgeNetworkClient>>>();

    public NodeBridgeClient(NodeNetwork node, NodeLogDelegate logHandle, IEnumerable<string> wssUrls) : this(node, logHandle, wssUrls.Select(x => new Uri(x))) { }

    public NodeBridgeClient(NodeNetwork node, NodeLogDelegate logHandle, IEnumerable<Uri> wssUrls)
    {
        this.node = node;
        this.logHandle = logHandle;
        this.wssUrls = wssUrls;
    }

    public int Connect(string serverIdentity, Guid roomId, string sessionIdentity, int maxCount = 1, int connectionTimeout = 2000)
    {
        var serverCount = tryConnect(maxCount, connectionTimeout);

        if (serverCount > 0)
        {
            if (trySign(serverIdentity, roomId, sessionIdentity))
                return serverCount;
        }

        return 0;
    }

    private int tryConnect(int maxCount, int connectionTimeout)
    {
        foreach (var item in connections)
        {
            if (item.Value.GetState())
                item.Value.Disconnect();
        }

        connections.Clear();

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
                            logHandle?.Invoke(LoggerLevel.Info, $"[Bridge Server] Send {pid}");
                        });

                        builder.AddReceiveHandle((c, pid, len) =>
                        {
                            logHandle?.Invoke(LoggerLevel.Info, $"[Bridge Server] Receive {pid}");
                        });
                    }
                    //builder.AddSendHandleForUnity((c, pid, len, st) =>
                    //{
                    //    Debug.Log($"Send {pid} to bridge client");
                    //}); //todo

                    //builder.AddReceiveHandleForUnity((c, pid, len) =>
                    //{
                    //    Debug.Log($"Receive {pid} from bridge client");
                    //}); // todo

                    builder.AddConnectHandle(client => client.Url = uri);
                    builder.AddPacketHandle(NodeBridgeClientPacketEnum.SignSessionResultPID, OnSignSessionReceive);
                })
                .WithUrl(uri)
                .Build());

        var count = 0;

        foreach (var item in bridgeServers)
        {
            if (!item.Value.Connect(connectionTimeout))
                continue;

            count++;

            connections.Add(item.Key, item.Value);

            if (count == maxCount)
                break;
        }

        return count;
    }

    private bool trySign(string serverIdentity, Guid roomId, string sessionIdentity)
    {
        var packet = OutputPacketBuffer.Create(NodeBridgeClientPacketEnum.SignSessionPID);

        packet.WriteString16(serverIdentity);
        packet.WriteGuid(roomId);
        packet.WriteString16(sessionIdentity);

        bool any = false;

        foreach (var item in connections)
        {
            if (!item.Value.GetState())
                continue;

            item.Value.Send(packet, false);

            any = true;
        }

        packet.Dispose();

        return any;
    }

    private void OnSignSessionReceive(BridgeNetworkClient client, InputPacketBuffer data)
    {
        var result = data.ReadBool();

        var sessions = result ? data.ReadCollection(() => TransportSessionInfoModel.Read(data)) : null;

        OnAvailableBridgeServersResult(result, this, client.Url, sessions);
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

    public OnReceiveSignSessionResultDelegate OnAvailableBridgeServersResult = (result, instance, from, servers) => { };
}
