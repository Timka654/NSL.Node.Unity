using NSL.Node.RoomServer.Shared.Client.Core;
using UnityEngine;

public class UnityNodeBehaviour : MonoBehaviour, INodeOwneredObject
{
    [SerializeField] private IRoomInfo nodeRoom;

    [SerializeField] private INodeClientNetwork nodePlayer;

    public IRoomInfo NodeRoom => nodeRoom;

    public INodeClientNetwork NodePlayer => nodePlayer;

    public void SetOwner(IRoomInfo nodeRoom, INodeClientNetwork nodePlayer)
    {
        this.nodeRoom = nodeRoom;
        this.nodePlayer = nodePlayer;
    }
}

