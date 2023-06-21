using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.Utils.Unity;
using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityNodeNetwork : NodeNetwork<GameInfo> { }

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

    public void FillOwner(GameObject obj, Guid nodeId)
    {
        var node = GetNode(nodeId);

        if (node == null)
            throw new KeyNotFoundException($"not found node with {nodeId}");

        foreach (var item in obj.GetComponents<UnityNodeBehaviour>())
        {
            item.SetOwner(this, node.Network);
        }
    }

    public void SetOwner(UnityNodeBehaviour obj, Guid nodeId)
    {
        var node = GetNode(nodeId);

        if (node == null)
            throw new KeyNotFoundException($"not found node with {nodeId}");

        obj.SetOwner(this, node.Network);
    }

    public override void Invoke(Action action, InputPacketBuffer buffer)
    {
        buffer.ManualDisposing = true;

        Invoke(() =>
        {
            action();
            buffer.Dispose();
        });
    }

    public override void Invoke(Action action)
    {
        ThreadHelper.InvokeOnMain(() =>
        {
            base.Invoke(action);
        });
    }
}
