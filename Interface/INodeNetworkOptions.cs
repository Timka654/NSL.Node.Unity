public interface INodeNetworkOptions
{
    NodeTransportModeEnum TransportMode { get; set; }

    int MaxNodesWaitCycle { get; set; }

    bool DebugPacketIO { get; set; }
}