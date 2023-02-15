using NSL.SocketClient;
using NSL.SocketCore.Extensions.Buffer;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RoomNetworkClient : BaseSocketNetworkClient
{
    public Uri Url { get; set; }

    public PacketWaitBuffer PacketWaitBuffer { get; }

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