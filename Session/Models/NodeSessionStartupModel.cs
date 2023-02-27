using NSL.SocketCore.Utils.Buffer;
using System;
using System.Collections.Generic;
using System.Linq;

public class NodeSessionStartupModel
{
    public string Token { get; protected set; }

    public Guid RoomId { get; protected set; }

    public string ServerIdentity { get; protected set; }

    public List<string> ConnectionEndPoints { get; protected set; }

    public NodeSessionStartupModel() { }

    public NodeSessionStartupModel(InputPacketBuffer buffer)
    {
        RoomId = buffer.ReadGuid();

        Token = buffer.ReadString16();

        ServerIdentity = buffer.ReadString16();

        ConnectionEndPoints = buffer.ReadCollection(() => buffer.ReadString16()).ToList();
    }
}
