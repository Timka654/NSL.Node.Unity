using NSL.UDP.Client.Info;
using System.Collections;
using System.Collections.Generic;
using System.Net;

public class NodeSessionBuilder
{
    private NodeSessionBuilder() { }

    public static NodeSessionBuilder Create()
        => new NodeSessionBuilder();

    public NodeSessionBuilder<TNodeOptions> WithOptions<TNodeOptions>()
        where TNodeOptions : NodeSessionOptions, new()
    => new NodeSessionBuilder<TNodeOptions>();

    public NodeSessionBuilder<NodeSessionOptions> WithDefaultOptions()
        => WithOptions<NodeSessionOptions>();
}

public class NodeSessionBuilder<TNodeOptions>
    where TNodeOptions : NodeSessionOptions, new()
{
    TNodeOptions options = new TNodeOptions();

    public NodeSessionBuilder<TNodeOptions> WithStunEndPoints(IEnumerable<StunServerInfo> endPoints)
    {
        options.StunServers = endPoints;

        return this;
    }

    public NodeSessionBuilder<TNodeOptions> WithStartupInfo(NodeSessionStartupModel info)
    {
        options.StartupInfo = info;

        return this;
    }

    //public NodeSessionBuilder<TNodeOptions> WithRequirementProxy(bool requirement = true)
    //{
    //    options.RequirementProxy = requirement;

    //    return this;
    //}

    public NodeSession<TNodeOptions> Build()
        => new NodeSession<TNodeOptions>(options);
}