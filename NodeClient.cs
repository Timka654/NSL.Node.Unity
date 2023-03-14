using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Utils;
using NSL.SocketCore.Utils.Buffer;
using NSL.UDP;
using NSL.UDP.Client;
using NSL.UDP.Enums;
using NSL.Utils;
using System;
using System.Net;

public class NodeClient : INetworkClient, INodeClientNetwork
{
    public INodeNetwork NodeNetwork { get; }

    public string Token => connectionInfo.Token;

    public Guid NodeId => connectionInfo.NodeId;

    public bool IsLocalNode => NodeNetwork.LocalNodeId == NodeId;

    public NodeRoomClient Proxy { get; }

    public string EndPoint => connectionInfo.EndPoint;

    public NodeClientStateEnum State { get; private set; }

    public event NodeClientStateChangeDelegate OnStateChanged = (nstate, ostate) => { };

    public NodeInfo NodeInfo { get; private set; }

    public NodeClient(
        NodeConnectionInfoModel connectionInfo,
        INodeNetwork nodeNetwork,
        NodeLogDelegate logHandle,
        NodeRoomClient proxy,
        UDPServer<UDPNodeServerNetworkClient> udpBindingPoint)
    {
        this.connectionInfo = connectionInfo;
        NodeNetwork = nodeNetwork;
        this.logHandle = logHandle;
        Proxy = proxy;
        this.udpBindingPoint = udpBindingPoint;
        NodeInfo = new NodeInfo(this, NodeId);
    }

    public bool TryConnect(NodeConnectionInfoModel connectionInfo)
    {
        if (State != NodeClientStateEnum.None && EndPoint.Equals(connectionInfo.EndPoint))
            return true;

        this.connectionInfo = connectionInfo;

        if (IsLocalNode)
        {
            State = NodeClientStateEnum.OnlyProxy;

            return true;
        }

        var oldState = State;

        if (string.IsNullOrWhiteSpace(EndPoint) || NodeNetwork.TransportMode.Equals(NodeTransportModeEnum.ProxyOnly))
        {
            if (State == NodeClientStateEnum.Connected && udpClient != null)
            {
                udpClient.Disconnect();
            }

            State = NodeClientStateEnum.OnlyProxy;

            if (!oldState.Equals(State)) OnStateChanged(State, oldState);

            return true;
        }

        var point = NSLEndPoint.Parse(EndPoint);

        bool result = false;

        switch (point.ProtocolType)
        {
            case NSLEndPoint.Type.UDP:
                result = createUdp(point.Address, point.Port);
                break;
            default:
                throw new Exception($"Unsupported protocol {point.ProtocolType} for {nameof(NodeClient)} P2P connection");
        }

        State = result ? NodeClientStateEnum.Connected : NodeClientStateEnum.OnlyProxy;

        if (!oldState.Equals(State)) OnStateChanged(State, oldState);

        return result;
    }

    public void SendBroadcast(DgramOutputPacketBuffer packet, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered, bool disposeOnSend = true)
    {
        if (IsLocalNode)
            return;

        packet.Channel = channel;

        SendBroadcast(packet, disposeOnSend);
    }

    public void SendBroadcast(DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (IsLocalNode)
            return;

        packet.PacketId = (ushort)RoomPacketEnum.Broadcast;

        if (udpClient != null)
            udpClient.Send(packet, false);

        if (disposeOnSend)
            packet.Dispose();
    }
    public void SendBroadcast(Action<DgramOutputPacketBuffer> build, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered)
    {
        var packet = new DgramOutputPacketBuffer();

        packet.WriteGuid(NodeNetwork.LocalNodeId);

        build(packet);

        SendBroadcast(packet, channel);
    }

    public void SendBroadcast(Action<DgramOutputPacketBuffer> build, ushort code, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered)
    {
        SendBroadcast(p =>
        {
            p.WriteUInt16(code);
            build(p);
        }, channel);
    }


    public void Send(ushort code, Action<DgramOutputPacketBuffer> build, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered)
    {
        Send(p =>
        {
            p.WriteUInt16(code);
            build(p);
        }, channel);
    }

    public void Send(Action<DgramOutputPacketBuffer> build, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered)
    {
        if (IsLocalNode)
            return;

        var packet = new DgramOutputPacketBuffer();

        packet.WriteGuid(NodeId);

        packet.WriteGuid(NodeNetwork.LocalNodeId);

        build(packet);

        packet.WithPid(RoomPacketEnum.Transport);

        Send(packet, channel);
    }

    public void Send(ushort code, Action<DgramOutputPacketBuffer> build)
    {
        Send(p =>
        {
            p.WriteUInt16(code);
            build(p);
        });
    }

    public void Send(Action<DgramOutputPacketBuffer> build)
    {
        if (IsLocalNode)
            return;

        var packet = new DgramOutputPacketBuffer();

        packet.WriteGuid(NodeId);

        packet.WriteGuid(NodeNetwork.LocalNodeId);

        build(packet);

        packet.WithPid(RoomPacketEnum.Transport);

        Send(packet, true);
    }

    public void Send(DgramOutputPacketBuffer packet, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered, bool disposeOnSend = true)
    {
        if (IsLocalNode)
            return;

        packet.Channel = channel;

        Send(packet, disposeOnSend);
    }

    public void Send(DgramOutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (IsLocalNode)
            return;

        if (udpClient != null)
            udpClient.Send(packet, false);

        if (NodeNetwork.TransportMode.HasFlag(NodeTransportModeEnum.ProxyOnly))
            Proxy.SendToServers(packet);

        if (disposeOnSend)
            packet.Dispose();
    }

    private bool createUdp(string ip, int port)
    {
        var client = udpBindingPoint.CreateClientConnection(new System.Net.IPEndPoint(IPAddress.Parse(ip), port));

        client.Data.Node = this;

        udpClient = client;

        return true;
    }

    public override void Dispose()
    {
        base.Dispose();

        udpClient?.Disconnect();
    }

    private UDPClient<UDPNodeServerNetworkClient> udpClient;

    private NodeConnectionInfoModel connectionInfo;
    private readonly NodeLogDelegate logHandle;
    private readonly UDPServer<UDPNodeServerNetworkClient> udpBindingPoint;
}