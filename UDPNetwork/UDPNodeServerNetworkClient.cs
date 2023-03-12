using NSL.SocketClient;
using NSL.SocketCore.Utils.SystemPackets;
using NSL.SocketServer.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;

public class UDPNodeServerNetworkClient : IServerNetworkClient, IUDPClientWithPing<UDPNodeServerNetworkClient>
{
    public UDPPingPacket<UDPNodeServerNetworkClient> PingPacket { get; }

    public NodeClient Node { get; set; }

    public UDPNodeServerNetworkClient() : base()
    {
        PingPacket = new UDPPingPacket<UDPNodeServerNetworkClient>(this);
    }

}
