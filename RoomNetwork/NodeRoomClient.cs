using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.SocketCore.Unity;
using NSL.BuilderExtensions.WebSocketsClient;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Extensions.Buffer;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.WebSockets.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;

public class NodeRoomClient : IDisposable
{
    public delegate void OnReceiveSignSessionResultDelegate(bool result, NodeRoomClient instance, Uri from);
    public delegate void OnReceiveNodeListDelegate(IEnumerable<NodeConnectionInfoModel> nodes, NodeRoomClient instance);
    public delegate void OnExecuteDelegate(InputPacketBuffer buffer);
    public delegate void OnReceiveNodeTransportDelegate(Guid nodeId, InputPacketBuffer buffer);
    public delegate void OnRoomReadyDelegate(DateTime createTime, TimeSpan serverTimeOffset);

    private readonly IEnumerable<Uri> wssUrls;

    private readonly NodeNetwork node;
    private readonly NodeLogDelegate logHandle;

    private Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>> connections = new Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>>();

    public NodeRoomClient(NodeNetwork node, NodeLogDelegate logHandle, IEnumerable<string> wssUrls)
        : this(node, logHandle, wssUrls.Select(x => new Uri(x))) { }

    public NodeRoomClient(NodeNetwork node, NodeLogDelegate logHandle, IEnumerable<Uri> wssUrls)
    {
        this.node = node;
        this.logHandle = logHandle;
        this.wssUrls = wssUrls;
    }

    public NodeRoomClient(NodeNetwork node, NodeLogDelegate logHandle, string wssUrl)
        : this(node, logHandle, Enumerable.Repeat(wssUrl, 1).ToArray()) { }

    public async Task<int> Connect(Guid nodeIdentity, string sessionIdentity, string endPoint, int connectionTimeout = 2000)
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
                .WithClientProcessor<RoomNetworkClient>()
                .WithOptions<WSClientOptions<RoomNetworkClient>>()
                .WithCode(builder =>
                {
                    builder.AddConnectHandle(client =>
                    {
                        client.Url = uri;

                        client.PingPongEnabled = true;

                        var packet = OutputPacketBuffer.Create(RoomPacketEnum.SignSession);

                        packet.WriteString16(sessionIdentity);
                        packet.WriteGuid(nodeIdentity);
                        packet.WriteString16(endPoint);

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
                })
                .WithUrl(uri)
                .Build());

        var count = 0;

        foreach (var item in bridgeServers)
        {
            if (!await item.Value.ConnectAsync(connectionTimeout))
                continue;

            count++;

            connections.Add(item.Key, item.Value);
        }

        return count;
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

    private void OnSignSessionReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        var result = data.ReadBool();

        if (result)
            client.RequestServerTimeOffset();
        else
        {

        }

        OnSignOnServerResult(result, this, client.Url);
    }

    private void OnChangeNodeListReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnChangeNodeList(data.ReadCollection(() => new NodeConnectionInfoModel(data.ReadGuid(), data.ReadString16(), data.ReadString16())), this);
    }

    private void OnRoomReadyReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        var offset = client.ServerDateTimeOffset;

        if ((offset < TimeSpan.Zero && offset > TimeSpan.FromMilliseconds(-100)) || (offset > TimeSpan.Zero && offset < TimeSpan.FromMilliseconds(100)))
            offset = TimeSpan.Zero;

        OnRoomReady(data.ReadDateTime(), offset);
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

    public OnReceiveSignSessionResultDelegate OnSignOnServerResult = (result, instance, from) => { };

    public OnReceiveNodeListDelegate OnChangeNodeList = (data, transportClient) => { };

    public event OnRoomReadyDelegate OnRoomReady = (d, ts) => { };

    public event OnReceiveNodeTransportDelegate OnTransport = (nodeId, buffer) => { };

    public event OnExecuteDelegate OnExecute = (buffer) => { };

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
