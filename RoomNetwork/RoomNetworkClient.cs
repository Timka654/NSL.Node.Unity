using NSL.SocketClient;
using NSL.SocketCore.Extensions.Buffer;
using System;
using System.Threading;

public class RoomNetworkClient : BaseSocketNetworkClient
{
    public Uri ServerUrl { get; set; }

    public Guid LocalNodeIdentity => PlayerId;

    public Guid PlayerId { get; set; }

    private CancellationTokenSource LiveConnectionCTS = new CancellationTokenSource();

    public CancellationToken LiveConnectionToken => LiveConnectionCTS.Token;

    public bool IsSigned { get; set; }

    public override void Dispose()
    {
        base.Dispose();
        LiveConnectionCTS.Cancel();
    }
}