using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.UDPClient;
using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore;
using NSL.SocketCore.Utils;
using NSL.SocketCore.Utils.Buffer;
using NSL.UDP.Client;
using NSL.Utils;
using System;

public class NodeClient : INetworkClient, IPlayerNetwork
{
    public INodeNetwork NodeNetwork { get; }

    public string Token => connectionInfo.Token;

    public Guid PlayerId => connectionInfo.NodeId;

    public bool IsLocalNode => roomServer.LocalNodeIdentity == PlayerId;

    public NodeRoomClient Proxy { get; }

    public string EndPoint => connectionInfo.EndPoint;

    public NodeClientStateEnum State { get; private set; }

    public event NodeClientStateChangeDelegate OnStateChanged = (nstate, ostate) => { };

    public PlayerInfo PlayerInfo { get; private set; }

    public NodeClient(NodeConnectionInfoModel connectionInfo, RoomNetworkClient roomServer, INodeNetwork nodeNetwork, NodeRoomClient proxy)
    {
        this.connectionInfo = connectionInfo;
        this.roomServer = roomServer;
        NodeNetwork = nodeNetwork;
        Proxy = proxy;
        PlayerInfo = new PlayerInfo() { Id = PlayerId, Network = this };
    }

    private void Proxy_OnTransport(Guid playerId, InputPacketBuffer buffer)
    {
        if (playerId != PlayerId)
            return;

        NodeNetwork.Invoke(PlayerInfo, buffer);
    }

    public bool TryConnect(NodeConnectionInfoModel connectionInfo)
    {
        if (State != NodeClientStateEnum.None && EndPoint.Equals(connectionInfo.EndPoint))
            return true;

        this.connectionInfo = connectionInfo;

        var oldState = State;

        if (string.IsNullOrWhiteSpace(EndPoint) || NodeNetwork.TransportMode.Equals(NodeTransportModeEnum.ProxyOnly))
        {
            if (State == NodeClientStateEnum.Connected && udpNetwork != null)
            {
                udpNetwork.Disconnect();
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
        var packet = new OutputPacketBuffer();

        packet.WriteGuid(PlayerId);

        build(packet);

        packet.WithPid(RoomPacketEnum.Transport);

        Send(packet);
    }

    public void Send(OutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (udpClient != null)
            udpClient.Send(packet, false);

        if (NodeNetwork.TransportMode.HasFlag(NodeTransportModeEnum.ProxyOnly))
            Proxy.SendToServers(packet);

        if (disposeOnSend)
            packet.Dispose();
    }

    private bool createUdp(string ip, int port)
    {
        udpNetwork = UDPClientEndPointBuilder.Create()
            .WithClientProcessor<NodeNetworkClient>()
            .WithOptions<UDPClientOptions<NodeNetworkClient>>()
            .UseEndPoint(ip, port)
            .WithCode(builder =>
            {
                builder.AddConnectHandle(client =>
                {
                    client.PingPongEnabled = true;
                });
            })
            .Build();

        udpNetwork.Connect();
        udpClient = udpNetwork.GetClient();
        return true;
    }

    private void OnReceiveTransportHandle(NodeNetworkClient client, InputPacketBuffer buffer)
    {
        buffer.ReadGuid();

        Proxy_OnTransport(PlayerId, buffer);
    }

    public override void Dispose()
    {
        base.Dispose();

        udpNetwork?.Disconnect();
    }

    private UDPNetworkClient<NodeNetworkClient> udpNetwork;

    private INetworkNode udpClient;

    private NodeConnectionInfoModel connectionInfo;
    private readonly RoomNetworkClient roomServer;
}