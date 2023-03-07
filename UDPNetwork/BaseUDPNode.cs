using NSL.BuilderExtensions.SocketCore;
using NSL.BuilderExtensions.UDPServer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.UDP.Client;
using NSL.UDP.Client.Info;
using NSL.UDP.Client.Interface;
using NSL.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using UnityEditor.Experimental.GraphView;

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
        NodeLogDelegate logHandle)
    {
        var endPoint = UDPServerEndPointBuilder
            .Create()
            .WithClientProcessor<UDPNodeServerNetworkClient>()
            .WithOptions<UDPServerOptions<UDPNodeServerNetworkClient>>()
            .WithBindingPoint(new IPEndPoint(IPAddress.Any, 0))
            .WithCode(builder =>
            {
                var options = builder.GetOptions() as ISTUNOptions;

                options.StunServers.AddRange(STUNServers);

                builder.AddExceptionHandle((ex, c) =>
                {
                    if (ex is ObjectDisposedException)
                        return;

                    logHandle(LoggerLevel.Error, ex.ToString());
                });

                builder.AddConnectHandle(client =>
                {
                    logHandle?.Invoke(LoggerLevel.Info, $"[UDP Binding Point] Connect new client");
                });

                builder.AddPacketHandle(1010, (client, data) => logHandle(LoggerLevel.Error, $"asafasfasfasfasfasfsafas"));

                if (node.DebugPacketIO)
                {
                    builder.AddSendHandle((c, pid, len, st) =>
                    {
                        logHandle?.Invoke(LoggerLevel.Info, $"[UDP Binding Point] Send {pid}");
                    });

                    builder.AddReceiveHandle((c, pid, len) =>
                    {
                        logHandle?.Invoke(LoggerLevel.Info, $"[UDP Binding Point] Receive {pid}");
                    });
                }
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
