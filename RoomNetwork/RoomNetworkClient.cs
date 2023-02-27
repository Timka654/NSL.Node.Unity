using NSL.SocketClient;
using NSL.SocketCore.Extensions.Buffer;
using System;

public class RoomNetworkClient : BaseSocketNetworkClient
{
    public Uri ServerUrl { get; set; }

    public PacketWaitBuffer PacketWaitBuffer { get; }

    public RoomSessionInfoModel SessionInfo { get; set; }

    public Guid LocalNodeIdentity => SessionInfo.Id;

    public RoomNetworkClient()
    {
        PacketWaitBuffer = new PacketWaitBuffer(this);
    }

    public override void Dispose()
    {
        PacketWaitBuffer.Dispose();

        base.Dispose();
    }
}