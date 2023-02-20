using NSL.SocketCore.Utils.Buffer;
using System;

public class TransportSessionInfoModel
{
    public TransportSessionInfoModel(string connectionUrl, Guid id)
    {
        ConnectionUrl = connectionUrl;
        Id = id;
    }

    public string ConnectionUrl { get; }

    public Guid Id { get; }

    public static TransportSessionInfoModel Read(InputPacketBuffer data)
        => new TransportSessionInfoModel(data.ReadString16(), data.ReadGuid());
}
