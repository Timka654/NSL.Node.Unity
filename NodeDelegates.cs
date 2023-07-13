using NSL.Node.Core.Models.Message;
using NSL.Node.Core.Models.Response;
using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using System;
using System.Collections.Generic;


public delegate void NodeClientStateChangeDelegate(NodeClientStateEnum newState, NodeClientStateEnum oldState);

public delegate void OnChangeRoomStateDelegate(NodeRoomStateEnum state);

public delegate void OnChangeNodesReadyDelegate(int current, int total);

public delegate void OnChangeNodesReadyDelayDelegate(int current, int total);

public delegate void NodeLogDelegate(LoggerLevel level, string content);

public delegate void OnNodeRoomExecuteDelegate(InputPacketBuffer buffer);

public delegate void OnNodeRoomTransportDelegate(Guid nodeId, InputPacketBuffer buffer);

public delegate void OnNodeRoomSignReceiveDelegate(RoomNetworkClient room,RoomNodeSignInResponseModel response);

public delegate void OnRoomNodeDisconnectDelegate(Guid nodeId);

public delegate void OnRoomNodeConnectionLostDelegate(Guid nodeId);

public delegate void OnRoomChangeNodeEndPointDelegate(Guid nodeId, string endPoint);

public delegate void OnRoomDestroyDelegate();

public delegate void OnRoomNodeConnectedDelegate(NodeRoomClient instance,ConnectNodeMessageModel message);