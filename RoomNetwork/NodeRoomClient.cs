using Cysharp.Threading.Tasks;
using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.WebSocketsClient;
using NSL.Node.Core.Models.Message;
using NSL.Node.Core.Models.Requests;
using NSL.Node.Core.Models.Response;
#if UNITY_WEBGL && !UNITY_EDITOR
using NSL.BuilderExtensions.WebSocketsClient.Unity;
#endif
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Extensions.Buffer;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.Utils.Unity;
using NSL.WebSockets.Client;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class NodeRoomClient : IDisposable
{
    private readonly INodeNetworkOptions node;
    private readonly NodeLogDelegate logHandle;
    private readonly OnChangeRoomStateDelegate changeStateHandle;
    private readonly NodeSessionStartupModel roomStartInfo;
    private readonly Dictionary<string, Guid> connectionPoints;
    private readonly string localNodeUdpEndPoint;
    private readonly Action onDisconnect;

    public int ConnectionTimeout { get; set; } = 10_000;

    private Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>> connections = new Dictionary<Uri, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>>>();

    public bool AnyServers() => connections.Any();

    public bool AnySignedServers() => connections.Values.Any(x => x.Data?.IsSigned == true);

    public NodeRoomClient(
        INodeNetworkOptions node,
        NodeLogDelegate logHandle,
        OnChangeRoomStateDelegate changeStateHandle,
        NodeSessionStartupModel roomStartInfo,
        Dictionary<string, Guid> connectionPoints,
        string localNodeUdpEndPoint,
        Action onDisconnect)
    {
        this.node = node;
        this.logHandle = logHandle;
        this.changeStateHandle = changeStateHandle;
        this.roomStartInfo = roomStartInfo;
        this.connectionPoints = connectionPoints;
        this.localNodeUdpEndPoint = localNodeUdpEndPoint;
        this.onDisconnect = onDisconnect;
    }

    public async Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        changeStateHandle(NodeRoomStateEnum.ConnectionTransportServers);

        if (await connectToServers(cancellationToken) == default)
            throw new Exception($"WaitAndRun : Can't find working transport servers");
    }

    private async Task<int> connectToServers(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Dispose();

        cancellationToken.ThrowIfCancellationRequested();

        var roomServers = connectionPoints.ToDictionary(
            point => new Uri(point.Key),
            point => WebSocketsClientEndPointBuilder.Create()
                .WithClientProcessor<RoomNetworkClient>()
                .WithOptions()
                .WithCode(builder =>
                {
                    var options = builder.GetCoreOptions();

                    builder.AddConnectHandle(client => client.InitializeObjectBag());

                    options.ConfigureRequestProcessor(RoomPacketEnum.Response);

                    builder.AddConnectHandle(client =>
                    {
                        client.ServerUrl = new Uri(point.Key);

                        client.PingPongEnabled = true;

                        SendSign(client, point.Value);
                    });

                    builder.AddDisconnectHandle(client => { client.Dispose(); onDisconnect(); });

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

                    builder.AddPacketHandle(RoomPacketEnum.TransportMessage, OnTransportReceive);
                    builder.AddPacketHandle(RoomPacketEnum.ExecuteMessage, OnExecuteReceive);
                    builder.AddPacketHandle(RoomPacketEnum.ReadyRoomMessage, OnRoomReadyReceive);

                    builder.AddPacketHandle(RoomPacketEnum.NodeConnectMessage, OnNodeConnectMessageReceive);
                    builder.AddPacketHandle(RoomPacketEnum.RoomDestroyMessage, OnNodeRoomDestroyMessageReceive);
                    builder.AddPacketHandle(RoomPacketEnum.NodeConnectionLostMessage, OnNodeConnectionLostReceive);
                    builder.AddPacketHandle(RoomPacketEnum.NodeDisconnectMessage, OnNodeDisconnectReceive);
                    builder.AddPacketHandle(RoomPacketEnum.NodeChangeEndPointMessage, OnNodeChangeEndPointReceive);
                })
                .WithUrl(new Uri(point.Key))

#if UNITY_WEBGL && !UNITY_EDITOR
                .BuildForWGLPlatform()
#else
                .Build()
#endif
                );

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in roomServers)
        {
            if (await ConnectToServer(item.Key, item.Value, cancellationToken))
                connections.Add(item.Key, item.Value);
        }

        return connections.Count;
    }

    private async Task<bool> ConnectToServer(Uri url, WSNetworkClient<RoomNetworkClient, WSClientOptions<RoomNetworkClient>> network, CancellationToken cancellationToken)
    {
        logHandle?.Invoke(LoggerLevel.Info, $"Try connect to {url}");

        if (!await network.ConnectAsync(ConnectionTimeout))
        {
            logHandle?.Invoke(LoggerLevel.Info, $"Cannot connect to {url}");

            return false;
        }

        logHandle?.Invoke(LoggerLevel.Info, $"Success connect to {url}");
        cancellationToken.ThrowIfCancellationRequested();

        return true;
    }

    #region Send

    private void SendDisconnectMessage(RoomNetworkClient client)
    {
        SendToServers(OutputPacketBuffer.Create(RoomPacketEnum.DisconnectMessage));
    }

    private void SendNodeChangeEndPointMessage(RoomNetworkClient client, string endPoint)
    {
        var packet = OutputPacketBuffer.Create(RoomPacketEnum.DisconnectMessage);

        packet.WriteString(endPoint);

        SendToServers(packet);
    }

    private void SendSign(RoomNetworkClient client, Guid sessionId)
    {
        var requestProcessor = client.GetRequestProcessor();

        var packet = RequestPacketBuffer.Create(RoomPacketEnum.SignSessionRequest);

        new RoomNodeSignInRequestModel()
        {
            SessionId = sessionId,
            RoomId = roomStartInfo.RoomId,
            Token = roomStartInfo.Token,
            ConnectionEndPoint = localNodeUdpEndPoint
        }.WriteFullTo(packet);

        requestProcessor.SendRequest(packet, (data) =>
        {
            var result = RoomNodeSignInResponseModel.ReadFullFrom(data);

            if (result.Success)
            {
                client.RequestServerTimeOffset();

                client.PlayerId = result.NodeId.Value;
                client.IsSigned = true;

                logHandle(LoggerLevel.Info, $"Success signed on {client.ServerUrl} - {sessionId}");
            }

            OnSignIn(client, result);

            if (result.Success)
            {
                client.IsSigned = true;
            }
            else
            {
                logHandle(LoggerLevel.Error, $"Cannot sign on {nameof(NodeRoomClient)}({client.ServerUrl} - {sessionId})");
                connections.Remove(client.ServerUrl);
                client.Network.Disconnect();
            }


            return true;
        });
    }

    public async Task<bool> SendReady(int totalCount, IEnumerable<Guid> readyNodes)
    {
        var p = RequestPacketBuffer.Create(RoomPacketEnum.ReadyNodeRequest);

        new RoomNodeReadyRequestModel()
        {
            ConnectedNodes = readyNodes.ToList(),
            ConnectedNodesCount = totalCount
        }.WriteFullTo(p);

        bool state = false;

        foreach (var item in connections)
        {
            if (item.Value.Data?.IsSigned != true)
                continue;

            CancellationTokenSource cts = new CancellationTokenSource();

            item.Value.Data.GetRequestProcessor().SendRequest(p, data =>
            {
                state = data?.ReadBool() == true;

                cts.Cancel();

                return true;
            }, false);

            try { await UniTask.Delay(10_000, cancellationToken: CancellationTokenSource.CreateLinkedTokenSource(cts.Token, item.Value.Data.LiveConnectionToken).Token); } catch (OperationCanceledException) { }

            if (!state)
                return state;
        }

        return state;
    }

    public void SendToServers(OutputPacketBuffer packet)
    {
        foreach (var item in connections)
        {
            if (!item.Value.GetState())
                continue;

            ((OutputPacketBuffer)packet).Send(item.Value, false);
        }
    }

    #endregion

    #region ReceiveHandles

    private void OnRoomReadyReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        var offset = client.ServerDateTimeOffset;

        if ((offset < TimeSpan.Zero && offset > TimeSpan.FromSeconds(-1)) || (offset > TimeSpan.Zero && offset < TimeSpan.FromSeconds(1)))
            offset = TimeSpan.Zero;

        var createTime = data.ReadDateTime();

#if DEBUG
        logHandle(LoggerLevel.Debug, $"{nameof(OnRoomReadyReceive)} - {createTime} - {offset}");
#endif

        changeStateHandle(NodeRoomStateEnum.Ready);
    }

    private void OnTransportReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        data.ReadGuid(); // local node

        var nid = data.ReadGuid(); // from node

        var len = (int)(data.Length - data.Position);

        var packet = new InputPacketBuffer(data.Read(len));

        OnTransport(nid, packet);
    }

    private void OnExecuteReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnExecute(data);
    }

    private void OnNodeConnectMessageReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnNodeConnect(this, ConnectNodeMessageModel.ReadFullFrom(data));
    }

    private void OnNodeRoomDestroyMessageReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnRoomDestroy();
    }

    private void OnNodeConnectionLostReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnNodeConnectionLost(data.ReadGuid());
    }

    private void OnNodeDisconnectReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnNodeDisconnect(data.ReadGuid());
    }

    private void OnNodeChangeEndPointReceive(RoomNetworkClient client, InputPacketBuffer data)
    {
        OnNodeChangeEndPoint(data.ReadGuid(), data.ReadString());
    }

    #endregion


    public event OnNodeRoomTransportDelegate OnTransport = (nodeId, buffer) => { };

    public event OnNodeRoomExecuteDelegate OnExecute = (buffer) => { };

    public event OnNodeRoomSignReceiveDelegate OnSignIn = (room, response) => { };

    public event OnRoomNodeConnectedDelegate OnNodeConnect = (instance, message) => { };

    public event OnRoomDestroyDelegate OnRoomDestroy = () => { };

    public event OnRoomNodeConnectionLostDelegate OnNodeConnectionLost = (nodeId) => { };

    public event OnRoomNodeConnectionLostDelegate OnNodeDisconnect = (nodeId) => { };

    public event OnRoomChangeNodeEndPointDelegate OnNodeChangeEndPoint = (nodeId, endPoint) => { };


    public void Dispose()
    {
        foreach (var item in connections)
        {
            if (item.Value.GetState())
            {
                item.Value.Disconnect();

                item.Value.Data.Dispose();
            }
        }

        connections.Clear();
    }
}
