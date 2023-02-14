using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.SocketCore.Unity;
using NSL.BuilderExtensions.WebSocketsClient;
using NSL.Node.BridgeServer.Shared.Enums;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketClient;
using NSL.SocketCore.Extensions.Buffer;
using NSL.SocketCore.Utils.Buffer;
using NSL.WebSockets.Client;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;

public class NodeTransportClient
{
    public delegate void OnReceiveSignSessionResultDelegate(bool result, NodeTransportClient instance, Uri from);
    public delegate void OnReceiveNodeListDelegate(IEnumerable<NodeConnectionInfo> nodes, NodeTransportClient instance);
    public delegate void OnReceiveNodeTransportDelegate(Guid nodeId, InputPacketBuffer buffer);
    public delegate void OnRoomAllNodesReadyDelegate(DateTime createTime, TimeSpan serverTimeOffset);

    private readonly IEnumerable<Uri> wssUrls;

    private Dictionary<Uri, WSNetworkClient<TransportNetworkClient, WSClientOptions<TransportNetworkClient>>> connections = new Dictionary<Uri, WSNetworkClient<TransportNetworkClient, WSClientOptions<TransportNetworkClient>>>();

    public NodeTransportClient(IEnumerable<string> wssUrls) : this(wssUrls.Select(x => new Uri(x))) { }

    public NodeTransportClient(IEnumerable<Uri> wssUrls) { this.wssUrls = wssUrls; }

    public NodeTransportClient(string wssUrl) : this(Enumerable.Repeat(wssUrl, 1).ToArray()) { }

    public NodeTransportClient(Uri wssUrl) : this(Enumerable.Repeat(wssUrl, 1).ToArray()) { }

    public int Connect(Guid nodeIdentity, string sessionIdentity, string endPoint, int connectionTimeout = 2000)
    {
        var serverCount = tryConnect(connectionTimeout);

        if (serverCount > 0)
        {
            if (trySign(nodeIdentity, sessionIdentity, endPoint))
                return serverCount;
        }

        return 0;
    }


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

    public void Transport(OutputPacketBuffer packet)
    {
        foreach (var item in connections)
        {
            if (!item.Value.GetState())
                continue;

            item.Value.Send(packet, false);
        }
    }

    private int tryConnect(int connectionTimeout)
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
                .WithClientProcessor<TransportNetworkClient>()
                .WithOptions<WSClientOptions<TransportNetworkClient>>()
                .WithCode(builder =>
                {
                    builder.AddConnectHandle(c =>
                    {
                        c.PingPongEnabled = true;
                    });

                    builder.AddSendHandleForUnity((c, pid, len, st) =>
                    {
                        Debug.Log($"Send {pid} to transport client");
                    });

                    builder.AddReceiveHandleForUnity((c, pid, len) =>
                    {
                        Debug.Log($"Receive {pid} from transport client");
                    });

                    builder.AddConnectHandle(client => client.Url = uri);
                    builder.AddPacketHandle(RoomPacketEnum.SignSessionResult, OnSignSessionReceive);
                    builder.AddPacketHandle(RoomPacketEnum.ChangeNodeList, OnChangeNodeListReceive);
                    builder.AddPacketHandle(RoomPacketEnum.Transport, OnTransportReceive);
                    builder.AddReceivePacketHandle(RoomPacketEnum.ReadyNodeResult, c => c.PacketWaitBuffer);
                    builder.AddPacketHandle(RoomPacketEnum.ReadyRoom, OnRoomReadyReceive);
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
        }

        return count;
    }

    private bool trySign(Guid nodeIdentity, string sessionIdentity, string endPoint)
    {
        var packet = OutputPacketBuffer.Create(RoomPacketEnum.SignSession);

        packet.WriteString16(sessionIdentity);
        packet.WriteGuid(nodeIdentity);
        packet.WriteString16(endPoint);

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

    private void OnTransportReceive(TransportNetworkClient client, InputPacketBuffer data)
    {
        var nid = data.ReadGuid();

        var len = (int)(data.Lenght - data.Position);

        var packet = new InputPacketBuffer(data.Read(len));

        OnTransport(nid, packet);
    }

    private void OnChangeNodeListReceive(TransportNetworkClient client, InputPacketBuffer data)
    {
        OnChangeNodeList(data.ReadCollection(() => new NodeConnectionInfo(data.ReadGuid(), data.ReadString16(), data.ReadString16())), this);
    }

    private void OnSignSessionReceive(TransportNetworkClient client, InputPacketBuffer data)
    {
        var result = data.ReadBool();

        if (result)
            client.RequestServerTimeOffset();

        OnSignOnServerResult(result, this, client.Url);
    }

    private void OnRoomReadyReceive(TransportNetworkClient client, InputPacketBuffer data)
    {
        var offset = client.ServerDateTimeOffset;

        if (offset < TimeSpan.Zero)
        {
            if (offset > TimeSpan.FromMilliseconds(-100))
                offset = TimeSpan.Zero;
        }
        else
        {
            if (offset < TimeSpan.FromMilliseconds(100))
                offset = TimeSpan.Zero;
        }

        OnRoomAllNodesReady(data.ReadDateTime(), offset);
    }

    public OnReceiveSignSessionResultDelegate OnSignOnServerResult = (result, instance, from) => { };
    public OnReceiveNodeListDelegate OnChangeNodeList = (data, transportClient) => { };
    public event OnReceiveNodeTransportDelegate OnTransport = (nodeId, buffer) => { };

    public event OnRoomAllNodesReadyDelegate OnRoomAllNodesReady = (d, ts) => { };

    private class TransportNetworkClient : BaseSocketNetworkClient
    {
        public Uri Url { get; set; }

        public PacketWaitBuffer PacketWaitBuffer { get; }

        public TransportNetworkClient()
        {
            PacketWaitBuffer = new PacketWaitBuffer(this);
        }

        public override void Dispose()
        {
            PacketWaitBuffer.Dispose();

            base.Dispose();
        }
    }

    public class NodeConnectionInfo
    {
        public Guid NodeId { get; }

        public string EndPoint { get; }

        public string Token { get; }

        public NodeConnectionInfo(Guid nodeId, string token, string endPoint)
        {
            this.NodeId = nodeId;
            this.EndPoint = endPoint;
            Token = token;
        }
    }
}
