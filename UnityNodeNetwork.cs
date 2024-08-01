using Cysharp.Threading.Tasks;
using NSL.Node.RoomServer.Shared.Client.Core;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using NSL.Utils.Unity;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class UnityNodeNetwork : NodeNetwork
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

    protected override async Task DelayHandle(int milliseconds, CancellationToken cancellationToken, bool _throw = true)
    {
        try { await UniTask.Delay(milliseconds, cancellationToken: cancellationToken); } catch (OperationCanceledException) { if (_throw) throw; }
    }

    public void FillOwner(GameObject obj, string nodeId)
    {
        var node = GetNode(nodeId);

        if (node == null)
            throw new KeyNotFoundException($"not found node with {nodeId}");

        foreach (var item in obj.GetComponents<UnityNodeBehaviour>())
        {
            item.SetOwner(this, node.Network);
        }
    }

    public void SetOwner(UnityNodeBehaviour obj, string nodeId)
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
