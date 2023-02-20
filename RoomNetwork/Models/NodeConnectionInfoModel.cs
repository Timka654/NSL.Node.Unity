using System;

public class NodeConnectionInfoModel
{
    public Guid NodeId { get; }

    public string EndPoint { get; }

    public string Token { get; }

    public NodeConnectionInfoModel(Guid nodeId, string token, string endPoint)
    {
        this.NodeId = nodeId;
        this.EndPoint = endPoint;
        Token = token;
    }
}