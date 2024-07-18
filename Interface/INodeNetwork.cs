using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.SocketCore.Utils.Buffer;
using NSL.UDP;
using System;
using System.Threading.Tasks;

public interface INodeNetwork : INodeNetworkOptions
{
    Guid LocalNodeId { get; }

    event OnChangeRoomStateDelegate OnChangeRoomState;
    event IRoomInfo.OnNodeDelegate OnNodeConnect;
    event Func<Task> OnRoomReady;

    DgramOutputPacketBuffer CreateSendToPacket(ushort command);
    OutputPacketBuffer CreateSendToServerPacket(ushort command);

    void Broadcast(DgramOutputPacketBuffer packet, bool disposeOnSend = true);

    bool SendTo(Guid nodeId, DgramOutputPacketBuffer packet, bool disposeOnSend = true);
    bool SendTo(NodeInfo node, DgramOutputPacketBuffer packet, bool disposeOnSend = true);

    void Invoke(NodeInfo nodeInfo, InputPacketBuffer buffer);
    void Invoke(Action action, InputPacketBuffer buffer);
}