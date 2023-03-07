using NSL.SocketCore.Utils.Buffer;
using System;

public class RoomSessionInfoModel
{
    public RoomSessionInfoModel(string connectionUrl, Guid id)
    {
        ConnectionUrl = connectionUrl;
        Id = id;
    }

    public string ConnectionUrl { get; }

    public Guid Id { get; }

    public static RoomSessionInfoModel Read(InputPacketBuffer data)
        => new RoomSessionInfoModel(data.ReadString16(), data.ReadGuid());
}
