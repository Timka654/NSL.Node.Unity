using NSL.Extensions.Session;
#if UNITY_WEBGL && !UNITY_EDITOR
using NSL.BuilderExtensions.WebSocketsClient.Unity;
#endif
using NSL.SocketCore;
using System;

public class RoomConnectionInfo
{
    public string Url { get; set; }

    public Guid SessionId { get; set; }

    public IClient NetworkClient { get; set; }

    public RoomNetworkClient Data => NetworkClient.GetUserData() as RoomNetworkClient;

    public NSLSessionInfo SessionInfo { get; set; }

    public DateTime? DisconnectTime { get; set; }

    public Guid TempId { get; } = Guid.NewGuid();

    public override string ToString()
        => $"{SessionId}~{Url}~{TempId}";
}
