using NSL.SocketServer.Utils;

public class UDPNodeServerNetworkClient : IServerNetworkClient, IUDPClientWithPing<UDPNodeServerNetworkClient>
{
    public UDPPingPacket<UDPNodeServerNetworkClient> PingPacket { get; }

    public NodeClient Node { get; set; }

    public UDPNodeServerNetworkClient() : base()
    {
        PingPacket = new UDPPingPacket<UDPNodeServerNetworkClient>(this);
    }

}
