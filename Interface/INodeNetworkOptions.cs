public interface INodeNetworkOptions
{
    NodeTransportModeEnum TransportMode { get; set; }

    int MaxReadyWaitDelay { get; set; }

    bool DebugPacketIO { get; set; }
}