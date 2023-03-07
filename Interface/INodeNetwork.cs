using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.SocketCore.Utils.Buffer;
using System;

public interface INodeNetwork : INodeNetworkOptions
{
    void Invoke(PlayerInfo nodePlayer, InputPacketBuffer buffer);
    void Invoke(Action action, InputPacketBuffer buffer);
}