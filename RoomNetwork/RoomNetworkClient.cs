using NSL.SocketClient;
using NSL.SocketCore.Extensions.Buffer;
using System;
using System.Threading;

public class RoomNetworkClient : BaseSocketNetworkClient
{
    public string ServerUrl { get; set; }

    public string LocalNodeIdentity => PlayerId;

    public string PlayerId { get; set; }

    private CancellationTokenSource LiveConnectionCTS = new CancellationTokenSource();

    public CancellationToken LiveConnectionToken => LiveConnectionCTS.Token;

    public bool IsSigned { get; set; }

    public override void Dispose()
    {
        base.Dispose();
        LiveConnectionCTS.Cancel();
    }
}