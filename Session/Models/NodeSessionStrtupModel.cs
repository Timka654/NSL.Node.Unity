using NSL.SocketCore.Utils.Buffer;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static NodeLobbyNetwork;

public class NodeSessionStartupModel
{
    public string Token { get; }

    public Guid RoomId { get; }

    public string ServerIdentity { get; }

    public List<string> ConnectionEndPoints { get; }

    public NodeSessionStartupModel(InputPacketBuffer buffer)
    {
        RoomId = buffer.ReadGuid();

        Token = buffer.ReadString16();

        ServerIdentity = buffer.ReadString16();

        ConnectionEndPoints = buffer.ReadCollection(() => buffer.ReadString16()).ToList();
    }
}
