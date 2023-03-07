using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.UDPClient;
using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore;
using NSL.SocketCore.Utils;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.SystemPackets;
using NSL.UDP;
using NSL.UDP.Client;
using NSL.UDP.Client.Interface;
using NSL.Utils;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

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
        PlayerInfo = new PlayerInfo(this, PlayerId);
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

    private async void RunPing(UDPClient<UDPNodeServerNetworkClient> client)
    {
        while (udpClient == client)
        {
            var packet = new DgramPacket();
            packet.PacketId = AliveConnectionPacket.PacketId;

            client.Send(packet);

            await Task.Delay(1_000);
        }
    }

    private bool createUdp(string ip, int port)
    {
        var bindPoint = udpBindingPoint.GetOptions() as IBindingUDPOptions;

        var client = new UDPClient<UDPNodeServerNetworkClient>(new System.Net.IPEndPoint(IPAddress.Parse(ip), port), udpBindingPoint.GetSocket(), udpBindingPoint.GetServerOptions());

        udpClient = client;

        try
        {
            RunPing(client);
            //udpClient.Connect();

        }
        catch (Exception ex)
        {
            logHandle(NSL.SocketCore.Utils.Logger.Enums.LoggerLevel.Error, ex.ToString());
            throw;
        }

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

        udpClient?.Disconnect();
    }

    private INetworkNode udpClient;

    private NodeConnectionInfoModel connectionInfo;
    private readonly RoomNetworkClient roomServer;
    private readonly NodeLogDelegate logHandle;
    private readonly UDPServer<UDPNodeServerNetworkClient> udpBindingPoint;
}