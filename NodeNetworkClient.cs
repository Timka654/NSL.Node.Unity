using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.SocketClient;
using NSL.UDP.Client;
using NSL.UDP;

public class NodeNetworkClient : INodeNetworkClient
{
    public UDPClient<UDPNodeServerNetworkClient> UDPClient;

    public int SendBytesRate => UDPClient.SendBytesRate;

    public int ReceiveBytesRate => UDPClient.ReceiveBytesRate;

    public int MINPing => UDPClient.ReliableChannel.MINPing;

    public int AVGPing => UDPClient.ReliableChannel.AVGPing;

    public int MAXPing => UDPClient.ReliableChannel.MAXPing;

    public void Send(DgramOutputPacketBuffer buffer, bool disposeOnSend = true)
    {
        UDPClient.Send(buffer, disposeOnSend);
    }

    public void Disconnect()
    {
        UDPClient.Disconnect();
    }
}