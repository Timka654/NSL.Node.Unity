using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.UDPServer;
using NSL.Node.RoomServer.Shared.Client.Core.Enums;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.UDP.Client;
using NSL.UDP.Client.Info;
using NSL.UDP.Client.Interface;
using NSL.Utils;
using System;
using System.Collections.Generic;
using System.Net;

public class BaseUDPNode
{
    public static readonly IEnumerable<StunServerInfo> STUNServers = new StunServerInfo[]
    {
        new StunServerInfo("stun.l.google.com:19302"),
        new StunServerInfo("stun1.l.google.com:19302"),
        new StunServerInfo("stun2.l.google.com:19302"),
        new StunServerInfo("stun3.l.google.com:19302"),
        new StunServerInfo("stun4.l.google.com:19302")
    };

    public static UDPServer<UDPNodeServerNetworkClient> CreateUDPEndPoint(
        INodeNetworkOptions node,
        Action<NSLEndPoint> getEndPoint,
        NodeLogDelegate logHandle,
        Action<Guid, InputPacketBuffer> transportHandle)
    {
        var endPoint = UDPServerEndPointBuilder
            .Create()
            .WithClientProcessor<UDPNodeServerNetworkClient>()
            .WithOptions<UDPClientOptions<UDPNodeServerNetworkClient>>()
            .WithBindingPoint(new IPEndPoint(IPAddress.Any, 0))
            .WithCode(builder =>
            {
                var options = builder.GetOptions() as ISTUNOptions;

                options.StunServers.AddRange(STUNServers);

                builder.GetOptions().RegisterUDPPingHandle();

                builder.AddExceptionHandle((ex, c) =>
                {
                    if (ex is ObjectDisposedException)
                        return;

                    logHandle(LoggerLevel.Error, ex.ToString());
                });

                builder.AddConnectHandle(client =>
                {
                    if (client == null)
                        return;

                    client.PingPacket.PingPongEnabled = true;

                    logHandle?.Invoke(LoggerLevel.Info, $"[UDP Binding Point] Connect new client {client.Network?.GetRemotePoint()}");
                });

                if (node.DebugPacketIO)
                {
                    builder.AddSendHandle((client, pid, len, st) =>
                    {
                        logHandle?.Invoke(LoggerLevel.Info, $"[UDP Binding Point] Send {pid} to {client?.GetRemotePoint()}");
                    });

                    builder.AddReceiveHandle((client, pid, len) =>
                    {
                        logHandle?.Invoke(LoggerLevel.Info, $"[UDP Binding Point] Receive {pid} from {client?.GetRemotePoint()}");
                    });
                }

                builder.AddPacketHandle(RoomPacketEnum.Broadcast, (client, data) =>
                {
                    var nid = data.ReadGuid(); // from node

                    transportHandle(nid, data);
                });

                builder.AddPacketHandle(RoomPacketEnum.Transport, (client, data) =>
                {
                    data.ReadGuid(); // local node

                    var nid = data.ReadGuid(); // from node

                    transportHandle(nid, data);
                });
            })
            .Build();

        endPoint.Start();

        NSLEndPoint endPointConnectionUrl = default;

        if (endPoint?.StunInformation != null)
            endPointConnectionUrl = NSLEndPoint.FromIPAddress(
                NSLEndPoint.Type.UDP,
                endPoint.StunInformation.PublicEndPoint.Address,
                endPoint.StunInformation.PublicEndPoint.Port
                );

        if (getEndPoint != null)
            getEndPoint(endPointConnectionUrl);

        return endPoint;
    }
}
