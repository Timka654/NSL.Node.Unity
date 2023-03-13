using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.SocketCore.Utils.Buffer;
using NSL.UDP;
using System;

public interface INodeNetwork : INodeNetworkOptions
{
    Guid LocalNodeId { get; }

    event OnChangeRoomStateDelegate OnChangeRoomState;
    event Action<NodeInfo> OnNodeConnect;
    event Action OnRoomReady;

    DgramOutputPacketBuffer CreateSendToPacket(ushort command);
    OutputPacketBuffer CreateSendToServerPacket(ushort command);

    void Broadcast(DgramOutputPacketBuffer packet, bool disposeOnSend = true);

    bool SendTo(Guid nodeId, DgramOutputPacketBuffer packet, bool disposeOnSend = true);
    bool SendTo(NodeInfo node, DgramOutputPacketBuffer packet, bool disposeOnSend = true);

    void Invoke(NodeInfo nodeInfo, InputPacketBuffer buffer);
    void Invoke(Action action, InputPacketBuffer buffer);
}