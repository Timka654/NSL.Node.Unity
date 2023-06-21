using NSL.SocketClient;
using NSL.SocketCore.Extensions.Buffer;
using System;

public class RoomNetworkClient : BaseSocketNetworkClient
{
    public Uri ServerUrl { get; set; }

    public RequestProcessor PacketWaitBuffer { get; }

    public Guid LocalNodeIdentity => PlayerId;

    public Guid PlayerId { get; set; }

    public RoomNetworkClient()
    {
        PacketWaitBuffer = new RequestProcessor(this);
    }

    public override void Dispose()
    {
        PacketWaitBuffer.Dispose();

        base.Dispose();
    }
}