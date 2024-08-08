using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.WebSocketsClient;
using NSL.Extensions.Session.Client;
#if UNITY_WEBGL && !UNITY_EDITOR
using NSL.BuilderExtensions.WebSocketsClient.Unity;
#endif
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore;
using NSL.SocketCore.Extensions.Buffer;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Exceptions;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.WebSockets.Client;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

public class NodeWSRoomClient : NodeRoomClient
{
    public NodeWSRoomClient(INodeNetworkOptions node, NodeLogDelegate logHandle, OnChangeRoomStateDelegate changeStateHandle, NodeSessionStartupModel roomStartInfo, Dictionary<string, Guid> connectionPoints, string localNodeUdpEndPoint, Func<int, CancellationToken, bool, Task> delayHandle, Action onDisconnect, Action onRecoverySession) : base(node, logHandle, changeStateHandle, roomStartInfo, connectionPoints, localNodeUdpEndPoint, delayHandle, onDisconnect, onRecoverySession)
    {
    }

    protected override Task<bool> ConnectAsync(IClient client, int connectionTimeout)
    {
        var c = client as WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>;

        return c.ConnectAsync(connectionTimeout);
    }

    protected override RoomConnectionInfo createConnection(string url, Guid sessionId)
    {
        connections.TryGetValue(url, out var oldCI);

        var connection = new RoomConnectionInfo()
        {
            Url = url,
            SessionId = sessionId,
            SessionInfo = oldCI?.SessionInfo,
            DisconnectTime = oldCI?.DisconnectTime
        };

        connection.NetworkClient = WebSocketsClientEndPointBuilder.Create()
                    .WithClientProcessor<RoomNetworkClient>()
                    .WithOptions()
                    .WithCode(builder =>
                    {
                        var options = builder.GetCoreOptions();

                        builder.AddConnectHandle(client => client.InitializeObjectBag());

                        options.ConfigureRequestProcessor(RoomPacketEnum.Response);

                        builder.GetWSClientOptions().AddNSLSessions();

                        builder.AddConnectHandle(client =>
                        {
                            client.IsSigned = false;

                            client.ServerUrl = url;

                            client.PingPongEnabled = true;

                            if (connection.SessionInfo != null)
                            {
                                TryRecoverySession(connection);
                            }
                            else
                            {
                                connections.Add(url, connection);
                                SendSign(connection);
                            }


                        });

                        builder.AddExceptionHandle((ex, c) =>
                        {
                            if (ex is ConnectionLostException
                            || ex is WebSocketException)
                                return;

                            logHandle?.Invoke(LoggerLevel.Error, $"[Room Server]({connection} - {roomStartInfo}) - {ex.ToString()}");
                        });

                        builder.AddDisconnectAsyncHandle(async client =>
                        {
                            logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Disconnect handle - {connection} - {roomStartInfo}");
                            if (!disposed && connection.SessionInfo != null)
                            {
                                logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Disconnect handle - {connection} - {roomStartInfo} - has session info");
                                if (!connection.DisconnectTime.HasValue)
                                {
                                    connection.DisconnectTime = DateTime.UtcNow;
                                    invokeConnectionLost(client.PlayerId);
                                }

                                if (connection.DisconnectTime.Value.Add(connection.SessionInfo.ExpiredSessionDelay) > DateTime.UtcNow)
                                {
                                    var nconnection = createConnection(url, sessionId);

                                    if (await ConnectToServer(nconnection, CancellationToken.None))
                                        connections[url] = nconnection;

                                    return;
                                }
                            }

                            client.Dispose();

                            connections.Remove(url);

                            onDisconnect();
                        });

                        if (node.DebugPacketIO)
                        {
                            builder.AddSendHandle((c, pid, len, st) =>
                            {
                                if (!InputPacketBuffer.IsSystemPID(pid))
                                    logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Send {pid}({Enum.GetName(typeof(RoomPacketEnum), pid)})");
                            });

                            builder.AddReceiveHandle((c, pid, len) =>
                            {
                                if (!InputPacketBuffer.IsSystemPID(pid))
                                    logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Receive {pid}({Enum.GetName(typeof(RoomPacketEnum), pid)})");
                            });
                        }

                        handlePackets(builder);
                    })
                    .WithUrl(new Uri(url))

#if UNITY_WEBGL && !UNITY_EDITOR
                .BuildForWGLPlatform()
#else
                    .Build()
#endif
                    ;

        return connection;
    }
}
