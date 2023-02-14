using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.UDPClient;
using NSL.Node.BridgeServer.Shared.Enums;
using NSL.Node.RoomServer.Shared;
using NSL.SocketClient;
using NSL.SocketCore;
using NSL.SocketCore.Utils;
using NSL.SocketCore.Utils.Buffer;
using NSL.UDP.Client;
using NSL.Utils;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using static NodeTransportClient;

public enum NodeClientState
{
    None,
    Connected,
    OnlyProxy,
}

public class NodeClient : INetworkClient, IPlayerNetwork
{
    public NodeNetwork NodeNetwork { get; }

    public string Token => connectionInfo.Token;

    public Guid PlayerId => connectionInfo.NodeId;

    public bool IsLocalNode => NodeNetwork.LocalNodeId == PlayerId;

    public NodeTransportClient Proxy { get; }

    public string EndPoint => connectionInfo.EndPoint;

    public NodeClientState State { get; private set; }

    public event NodeClientStateChangeDelegate OnStateChanged = (nstate, ostate) => { };

    public PlayerInfo PlayerInfo { get; private set; }

    public NodeClient(NodeConnectionInfo connectionInfo, NodeNetwork nodeNetwork, NodeTransportClient proxy)
    {
        this.connectionInfo = connectionInfo;

        NodeNetwork = nodeNetwork;
        Proxy = proxy;
        PlayerInfo = new PlayerInfo() { Id = PlayerId, Network = this };

        proxy.OnTransport += Proxy_OnTransport;
    }

    private void Proxy_OnTransport(Guid playerId, InputPacketBuffer buffer)
    {
        if (playerId != PlayerId)
            return;

        var code = buffer.ReadUInt16();

        var handle = NodeNetwork.GetHandle(code);

        if (handle == null)
            Debug.LogError($"{nameof(NodeClient)} Cannot find handle for code {code}");

        handle(PlayerInfo, buffer);
    }

    public bool TryConnect(NodeConnectionInfo connectionInfo)
    {
        if (State != NodeClientState.None && EndPoint.Equals(connectionInfo.EndPoint))
            return true;

        this.connectionInfo = connectionInfo;

        var oldState = State;

        if (string.IsNullOrWhiteSpace(EndPoint) || NodeNetwork.TransportMode.Equals(NodeTransportMode.ProxyOnly))
        {
            if (State == NodeClientState.Connected && udpNetwork != null)
            {
                udpNetwork.Disconnect();
            }

            State = NodeClientState.OnlyProxy;

            if (!oldState.Equals(State)) OnStateChanged(State, oldState);

            return true;
        }

        var point = NSLEndPoint.Parse(EndPoint);

        bool result = false;

        switch (point.ProtocolType)
        {
            case NSLEndPoint.Type.Unknown:
            case NSLEndPoint.Type.TCP:
            case NSLEndPoint.Type.WS:
                throw new Exception($"Unsupported protocol {point.ProtocolType} for {nameof(NodeClient)} P2P connection");
            case NSLEndPoint.Type.UDP:
                result = createUdp(point.Address, point.Port);
                break;
            default:
                break;
        }

        State = result ? NodeClientState.Connected : NodeClientState.OnlyProxy;

        if (!oldState.Equals(State)) OnStateChanged(State, oldState);

        return result;
    }

    public void Transport(Action<OutputPacketBuffer> build, ushort code)
    {
        Transport(p =>
        {
            p.WriteUInt16(code);
            build(p);
        });
    }

    public void Transport(Action<OutputPacketBuffer> build)
    {
        var packet = new OutputPacketBuffer();

        packet.WriteGuid(PlayerId);

        build(packet);

        packet.WithPid(NodeTransportPacketEnum.Transport);

        Send(packet);
    }

    public void Send(OutputPacketBuffer packet, bool disposeOnSend = true)
    {
        if (networkClient != null)
            networkClient.Send(packet, false);

        if (NodeNetwork.TransportMode.HasFlag(NodeTransportMode.ProxyOnly))
            Proxy.Transport(packet);

        if (disposeOnSend)
            packet.Dispose();
    }

    private bool createUdp(string ip, int port)
    {
        udpNetwork = UDPClientEndPointBuilder.Create()
            .WithClientProcessor<NodeNetworkClient>()
            .WithOptions<UDPClientOptions<NodeNetworkClient>>()
            .UseEndPoint(ip, port)
            .WithCode(builder =>
            {
                builder.AddConnectHandle(client =>
                {
                    client.PingPongEnabled = true;

                    networkClient = client.Network;
                });
            })
            .Build();

        udpNetwork.Connect();

        return true;
    }

    private void OnReceiveTransportHandle(NodeNetworkClient client, InputPacketBuffer buffer)
    {
        buffer.ReadGuid();

        Proxy_OnTransport(PlayerId, buffer);
    }

    #region Transport

    private uint pid = 0;

    private uint lClearPID = 0;

    private ConcurrentDictionary<uint, InputPacketBuffer> receiveBuffer = new ConcurrentDictionary<uint, InputPacketBuffer>();

    private void IncrementPid()
    {
        lock (this)
        {
            pid++;
        }
    }

    #endregion

    private UDPNetworkClient<NodeNetworkClient> udpNetwork;

    private INetworkNode networkClient;
    private NodeConnectionInfo connectionInfo;
}

public delegate void NodeClientStateChangeDelegate(NodeClientState newState, NodeClientState oldState);