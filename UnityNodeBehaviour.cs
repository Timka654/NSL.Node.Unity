using NSL.Node.RoomServer.Shared.Client.Core;
using UnityEngine;

public class UnityNodeBehaviour : MonoBehaviour
{
    [SerializeField] private INodeNetwork nodeNetwork;

    [SerializeField] private INodeClientNetwork node;

    public INodeNetwork NodeNetwork => nodeNetwork;

    public INodeClientNetwork Node => node;

    public void SetOwner(INodeNetwork nodeNetwork, INodeClientNetwork clientNetwork)
    {
        this.nodeNetwork = nodeNetwork;
        this.node = clientNetwork;
    }
}

