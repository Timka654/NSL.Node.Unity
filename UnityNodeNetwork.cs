using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.Utils.Unity;
using System;

public class UnityNodeNetwork<TRoomInfo> : NodeNetwork<TRoomInfo>
    where TRoomInfo : GameInfo
{
    protected override void LogHandle(LoggerLevel level, string content)
    {
        switch (level)
        {
            case LoggerLevel.Error:
                UnityEngine.Debug.LogError(content);
                break;
            case LoggerLevel.Info:
            case LoggerLevel.Log:
            case LoggerLevel.Debug:
            case LoggerLevel.Performance:
            default:
                UnityEngine.Debug.Log(content);
                break;
        }
    }

    public override void Invoke(Action action, InputPacketBuffer buffer)
    {
        buffer.ManualDisposing = true;

        ThreadHelper.InvokeOnMain(() =>
        {
            action();
            buffer.Dispose();
        });
    }
}
