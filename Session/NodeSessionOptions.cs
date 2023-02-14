using NSL.UDP.Client.Info;
using System;
using System.Collections.Generic;

public class NodeSessionOptions
{
    public IEnumerable<StunServerInfo> StunServers { get; set; }

    public NodeSessionStartupModel StartupInfo { get; set; }

    public TimeSpan BridgeConnectionTimeOut { get; set; } = TimeSpan.FromSeconds(2);

    public int BridgeMaxServerCount { get; set; }

    //public bool RequirementProxy { get; set; }
}