using NSL.SocketCore.Utils.Buffer;
using System;
using System.Collections.Generic;
using System.Linq;

public class NodeSessionStartupModel
{
    public string Token { get; set; }

    public Guid RoomId { get; set; }

    public Dictionary<string, Guid> ConnectionEndPoints { get; set; }

    public NodeSessionStartupModel() { }

    public NodeSessionStartupModel(InputPacketBuffer buffer)
    {
        RoomId = buffer.ReadGuid();

        Token = buffer.ReadString();

        ConnectionEndPoints = buffer.ReadCollection(() => (buffer.ReadString(), buffer.ReadGuid())).ToDictionary(x => x.Item1, x => x.Item2);
    }

    public override string ToString()
    {
        return $"{Token}~{RoomId}";
    }
}
