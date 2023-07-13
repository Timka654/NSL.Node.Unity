using NSL.SocketClient;
using NSL.SocketCore.Extensions.Buffer;
using System;

public class RoomNetworkClient : BaseSocketNetworkClient
{
    public Uri ServerUrl { get; set; }

    public Guid LocalNodeIdentity => PlayerId;

    public Guid PlayerId { get; set; }
}