using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class NodeSession : NodeSession<NodeSessionOptions>
{
    public NodeSession(NodeSessionOptions options) : base(options) { }
}

public class NodeSession<TNodeOptions> : IDisposable
    where TNodeOptions : NodeSessionOptions
{
    public TNodeOptions Options { get; }

    private NodeBridgeClient bridgeClient;

    List<NodeBridgeClient.TransportSessionInfo> connectionPoints = new List<NodeBridgeClient.TransportSessionInfo>();

    public NodeSession(TNodeOptions options)
    {
        Options = options;
    }

    public async Task LoadProxyEndPoints(CancellationToken cancellationToken)
    {
        try
        {
            connectionPoints.Clear();

            bridgeClient = new NodeBridgeClient(Options.StartupInfo.ConnectionEndPoints);

            
            using SemaphoreSlim locker = new SemaphoreSlim(0,Options.StartupInfo.ConnectionEndPoints.Count);

            bridgeClient.OnAvailableBridgeServersResult = (result, instance, from, servers) =>
            {
                if (result)
                    connectionPoints.AddRange(servers);

                locker.Release(1);
            };

            int serverCount = bridgeClient.Connect(
                Options.StartupInfo.ServerIdentity,
                Options.StartupInfo.RoomId,
                Options.StartupInfo.Token,
                Options.BridgeMaxServerCount,
                (int)Options.BridgeConnectionTimeOut.TotalMilliseconds);

            if (serverCount == default)
                throw new Exception("Must have any connection points");

            await locker.WaitAsync(10_000, cancellationToken);

            if (!connectionPoints.Any())
                throw new Exception($"WaitAndRun : Can't find working servers");

        }
        catch (TaskCanceledException)
        {
        }
    }

    public void Dispose()
    {
        bridgeClient?.Dispose();
    }
}