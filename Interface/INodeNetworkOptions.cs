public interface INodeNetworkOptions
{
    NodeTransportModeEnum TransportMode { get; set; }

    int MaxNodesWaitCycle { get; set; }

    int WaitBridgeDelayMS { get; set; }

    bool DebugPacketIO { get; set; }
}