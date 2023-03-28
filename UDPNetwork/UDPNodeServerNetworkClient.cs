using NSL.SocketServer.Utils;
using NSL.UDP.Packet;

public class UDPNodeServerNetworkClient : IServerNetworkClient, IUDPClientWithPing<UDPNodeServerNetworkClient>
{
    public UDPPingPacket<UDPNodeServerNetworkClient> PingPacket { get; }

    public NodeClient Node { get; set; }

    public UDPNodeServerNetworkClient() : base()
    {
        PingPacket = new UDPPingPacket<UDPNodeServerNetworkClient>(this);
    }

    public override void Dispose()
    {
        PingPacket.Dispose();
        base.Dispose();
    }
}
