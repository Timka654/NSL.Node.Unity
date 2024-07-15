using Cysharp.Threading.Tasks;
using NSL.BuilderExtensions.SocketCore;
using NSL.EndPointBuilder;
using NSL.Extensions.Session;
using NSL.Extensions.Session.Client.Packets;
using NSL.Node.Core.Models.Message;
using NSL.Node.Core.Models.Requests;
using NSL.Node.Core.Models.Response;
#if UNITY_WEBGL && !UNITY_EDITOR
using NSL.BuilderExtensions.WebSocketsClient.Unity;
#endif
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore;
using NSL.SocketCore.Extensions.Buffer;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.Utils.Unity;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ZergRush;

public abstract class NodeRoomClient : IDisposable
{
    protected readonly INodeNetworkOptions node;
    protected readonly NodeLogDelegate logHandle;
    protected readonly OnChangeRoomStateDelegate changeStateHandle;
    protected readonly NodeSessionStartupModel roomStartInfo;
    protected readonly Dictionary<string, Guid> connectionPoints;
    protected readonly string localNodeUdpEndPoint;
    protected readonly Action onDisconnect;
    protected readonly Action onRecoverySession;
    protected Func<int, CancellationToken, bool, Task> delayHandle;

    public int ConnectionTimeout { get; set; } = 10_000;

    protected Dictionary<string, RoomConnectionInfo> connections = new Dictionary<string, RoomConnectionInfo>();

    public bool AnyServers() => connections.Any();

    public bool AnySignedServers() => connections.Values.Any(x => x.Data?.IsSigned == true);

    public NodeRoomClient(
        INodeNetworkOptions node,
        NodeLogDelegate logHandle,
        OnChangeRoomStateDelegate changeStateHandle,
        NodeSessionStartupModel roomStartInfo,
        Dictionary<string, Guid> connectionPoints,
        string localNodeUdpEndPoint,
        Func<int, CancellationToken, bool, Task> delayHandle,
        Action onDisconnect,
        Action onRecoverySession)
    {
        this.node = node;
        this.logHandle = logHandle;
        this.changeStateHandle = changeStateHandle;
        this.roomStartInfo = roomStartInfo;
        this.connectionPoints = connectionPoints;
        this.localNodeUdpEndPoint = localNodeUdpEndPoint;
        this.delayHandle = delayHandle;
        this.onDisconnect = onDisconnect;
        this.onRecoverySession = onRecoverySession;
    }

    public async Task Initialize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (await connectToServers(cancellationToken) == default)
            throw new Exception($"WaitAndRun : Can't find working transport servers");
    }

    protected abstract RoomConnectionInfo createConnection(string url, Guid sessionId);

    protected abstract Task<bool> ConnectAsync(IClient client, int connectionTimeout);

    private async Task<int> connectToServers(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        clearConnections();

        cancellationToken.ThrowIfCancellationRequested();

        var roomServers = connectionPoints.ToDictionary(
            point => point.Key,
            point => createConnection(point.Key, point.Value));

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in roomServers)
        {
            if (await ConnectToServer(item.Value, cancellationToken))
                connections.Add(item.Key, item.Value);
        }

        return connections.Count;
    }

    protected async Task<bool> ConnectToServer(RoomConnectionInfo connection, CancellationToken cancellationToken)
    {
        var url = connection.Url;

        var network = connection.NetworkClient;

        logHandle?.Invoke(LoggerLevel.Info, $"Try connect to {url}");

        if (!await ConnectAsync(network, ConnectionTimeout))
        {
            logHandle?.Invoke(LoggerLevel.Info, $"Cannot connect to {url}");

            return false;
        }

        logHandle?.Invoke(LoggerLevel.Info, $"Success connect to {url}");
        cancellationToken.ThrowIfCancellationRequested();

        return true;
    }

    protected async void TryRecoverySession(RoomConnectionInfo connection)
    {
        logHandle?.Invoke(LoggerLevel.Info, $"[Room Server] Disconnect handle - {nameof(TryRecoverySession)}");

        var data = connection.Data;

        var session = connection.SessionInfo;

        await delayHandle(4_000, CancellationToken.None, false);

        data.NSLSessionSendRequest(response =>
        {
            logHandle?.Invoke(LoggerLevel.Info, $"Recovery session result - {response.Result.ToString()} {connection.Url}");

            if (response.Result == NSLRecoverySessionResultEnum.Ok)
            {
                connection.DisconnectTime = null;
                connection.SessionInfo = response.SessionInfo;
                onRecoverySession();
            }
            else
            {
                Dispose();
            }
        }, session);
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

    protected void SendSign(RoomConnectionInfo connectionInfo)
    {
        var client = connectionInfo.Data;

        var sessionId = connectionInfo.SessionId;

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
            var result = data.ReadNullableClass(() => new RoomNodeSignInResponseModel
            {
                NodeId = data.ReadNullable(() => data.ReadGuid()),
                Options = data.ReadNullableClass(() => data.ReadCollection(() =>
                {
                    string key = data.ReadString();
                    string value = data.ReadString();
                    return new { key, value };
                })?.ToDictionary(x => x.key, x => x.value)),
                SessionInfo = data.ReadNullableClass(() => new NSLSessionInfo
                {
                    ExpiredSessionDelay = data.ReadTimeSpan(),
                    RestoreKeys = data.ReadCollection(() => data.ReadString())?.ToArray(),
                    Session = data.ReadString()
                }),
                Success = data.ReadBool()
            });

            if (result.Success)
            {
                client.RequestServerTimeOffset();

                connectionInfo.SessionInfo = result.SessionInfo;

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

    private void SendClientDisconnect()
    {
        foreach (var item in connections.ToArray())
        {
            if (item.Value.Data?.IsSigned != true)
                continue;

            item.Value.NetworkClient.Send(OutputPacketBuffer.Create(RoomPacketEnum.DisconnectMessage));
        }
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

        foreach (var item in connections.ToArray())
        {
            for (int i = 0; i < 3 && item.Value.Data?.IsSigned != true; i++)
            {
                await delayHandle(1_000, item.Value.Data.LiveConnectionToken, false);
            }

            if (item.Value.Data?.IsSigned != true)
                continue;

            CancellationTokenSource cts = new CancellationTokenSource();

            item.Value.Data.GetRequestProcessor().SendRequest(p, data =>
            {
                state = data?.ReadBool() == true;

                cts.Cancel();

                return true;
            }, false);

            await delayHandle(4_000, CancellationTokenSource.CreateLinkedTokenSource(cts.Token, item.Value.Data.LiveConnectionToken).Token, false);

            if (!state)
            {
                Debug.LogError($"[Node] ({connections.IndexOf(item)}/{connections.Count}) State for connect to room = false {item.Key}");
                return state;
            }
        }

        if (state == false)
            Debug.LogError($"[Node] not any servers");

        return state;
    }

    public void SendToServers(OutputPacketBuffer packet)
    {
        foreach (var item in connections.ToArray())
        {
            if (!item.Value.NetworkClient.GetState())
                continue;

            ((OutputPacketBuffer)packet).Send(item.Value.NetworkClient, false);
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


    protected bool disposed { get; private set; } = false;

    public void Dispose()
    {
        disposed = true;

        clearConnections();
    }

    private void clearConnections()
    {
        SendClientDisconnect();

        foreach (var item in connections.Values.ToArray())
        {
            if (item.NetworkClient.GetState())
            {
                item.SessionInfo = null;

                item.NetworkClient.Disconnect();

                item.Data.Dispose();
            }
        }

        connections.Clear();
    }

    public void DevDisconnect()
    {
        foreach (var item in connections.Values.ToArray())
        {
            if (item.NetworkClient.GetState())
            {
                item.NetworkClient.Disconnect();
            }

        }
    }

    protected void invokeConnectionLost(Guid playerId)
        => OnNodeConnectionLost(playerId);

    protected void handlePackets(IOptionableEndPointBuilder<RoomNetworkClient> builder)
    {
        builder.AddPacketHandle(RoomPacketEnum.TransportMessage, OnTransportReceive);
        builder.AddPacketHandle(RoomPacketEnum.ExecuteMessage, OnExecuteReceive);
        builder.AddPacketHandle(RoomPacketEnum.ReadyRoomMessage, OnRoomReadyReceive);

        builder.AddPacketHandle(RoomPacketEnum.NodeConnectMessage, OnNodeConnectMessageReceive);
        builder.AddPacketHandle(RoomPacketEnum.RoomDestroyMessage, OnNodeRoomDestroyMessageReceive);
        builder.AddPacketHandle(RoomPacketEnum.NodeConnectionLostMessage, OnNodeConnectionLostReceive);
        builder.AddPacketHandle(RoomPacketEnum.NodeDisconnectMessage, OnNodeDisconnectReceive);
        builder.AddPacketHandle(RoomPacketEnum.NodeChangeEndPointMessage, OnNodeChangeEndPointReceive);
    }
}
