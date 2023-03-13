using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.UDPClient;
using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore;
using NSL.SocketCore.Utils;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.SocketCore.Utils.SystemPackets;
using NSL.UDP;
using NSL.UDP.Client;
using NSL.UDP.Client.Interface;
using NSL.UDP.Enums;
using NSL.UDP.Interface;
using NSL.Utils;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEditor.Experimental.GraphView;
using static UnityEditor.ObjectChangeEventStream;

public class NodeClient : INetworkClient, NSL.Node.RoomServer.Shared.Client.Core.INodeNetwork
{
    public INodeNetwork NodeNetwork { get; }

    public string Token => connectionInfo.Token;

    public Guid NodeId => connectionInfo.NodeId;

    public bool IsLocalNode => roomServer.LocalNodeIdentity == NodeId;

    public NodeRoomClient Proxy { get; }

    public string EndPoint => connectionInfo.EndPoint;

    public NodeClientStateEnum State { get; private set; }

    public event NodeClientStateChangeDelegate OnStateChanged = (nstate, ostate) => { };

    public NodeInfo NodeInfo { get; private set; }

    public NodeClient(
        NodeConnectionInfoModel connectionInfo,
        RoomNetworkClient roomServer,
        INodeNetwork nodeNetwork,
        NodeLogDelegate logHandle,
        NodeRoomClient proxy,
        UDPServer<UDPNodeServerNetworkClient> udpBindingPoint)
    {
        this.connectionInfo = connectionInfo;
        this.roomServer = roomServer;
        NodeNetwork = nodeNetwork;
        this.logHandle = logHandle;
        Proxy = proxy;
        this.udpBindingPoint = udpBindingPoint;
        NodeInfo = new NodeInfo(this, NodeId);
    }

    private void Proxy_OnTransport(Guid nodeId, InputPacketBuffer buffer)
    {
        if (nodeId != NodeId)
            return;

        NodeNetwork.Invoke(NodeInfo, buffer);
    }

    public bool TryConnect(NodeConnectionInfoModel connectionInfo)
    {
        if (State != NodeClientStateEnum.None && EndPoint.Equals(connectionInfo.EndPoint))
            return true;

        this.connectionInfo = connectionInfo;

        if (roomServer.LocalNodeIdentity == NodeId)
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

    public void Transport(Action<OutputPacketBuffer> build, ushort code)
    {
        Transport(p =>
        {
            p.WriteUInt16(code);
            build(p);
        });
    }

    public void Transport(Action<OutputPacketBuffer> build)
    {
        if (roomServer.LocalNodeIdentity == NodeId)
            return;

        var packet = new OutputPacketBuffer();

        packet.WriteGuid(NodeId);

        build(packet);

        packet.WithPid(RoomPacketEnum.Transport);

        Send(packet);
    }

    public void Send(OutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (roomServer.LocalNodeIdentity == NodeId)
            return;

        if (udpClient != null)
            udpClient.Send(packet, false);

        if (NodeNetwork.TransportMode.HasFlag(NodeTransportModeEnum.ProxyOnly))
            Proxy.SendToServers(packet);

        if (disposeOnSend)
            packet.Dispose();
    }

    public void Transport(Action<DgramPacket> build, ushort code, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered)
    {
        Transport(p =>
        {
            p.WriteUInt16(code);
            build(p);
        }, channel);
    }

    public void Transport(Action<DgramPacket> build, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered)
    {
        if (roomServer.LocalNodeIdentity == NodeId)
            return;

        var packet = new DgramPacket();

        packet.WriteGuid(NodeId);

        build(packet);

        packet.WithPid(RoomPacketEnum.Transport);

        Send(packet, channel);
    }

    public void Send(DgramPacket packet, UDPChannelEnum channel = UDPChannelEnum.ReliableOrdered, bool disposeOnSend = true)
    {
        if (roomServer.LocalNodeIdentity == NodeId)
            return;

        packet.Channel = channel;

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

    private void OnReceiveTransportHandle(NodeNetworkClient client, InputPacketBuffer buffer)
    {
        buffer.ReadGuid();

        Proxy_OnTransport(NodeId, buffer);
    }

    public override void Dispose()
    {
        base.Dispose();

        udpClient?.Disconnect();
    }

    private UDPClient<UDPNodeServerNetworkClient> udpClient;

    private NodeConnectionInfoModel connectionInfo;
    private readonly RoomNetworkClient roomServer;
    private readonly NodeLogDelegate logHandle;
    private readonly UDPServer<UDPNodeServerNetworkClient> udpBindingPoint;
}