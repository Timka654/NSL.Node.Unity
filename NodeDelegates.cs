using NSL.SocketCore.Utils.Buffer;
using NSL.SocketCore.Utils.Logger.Enums;
using System;
using System.Collections.Generic;


public delegate void NodeClientStateChangeDelegate(NodeClientStateEnum newState, NodeClientStateEnum oldState);

public delegate void OnChangeRoomStateDelegate(NodeRoomStateEnum state);

public delegate void OnChangeNodesReadyDelegate(int current, int total);

public delegate void OnChangeNodesReadyDelayDelegate(int current, int total);

public delegate void NodeLogDelegate(LoggerLevel level, string content);

public delegate void OnNodeRoomReceiveNodeListDelegate(RoomNetworkClient roomServer, IEnumerable<NodeConnectionInfoModel> nodes, NodeRoomClient instance);

public delegate void OnNodeRoomExecuteDelegate(InputPacketBuffer buffer);

public delegate void OnNodeRoomTransportDelegate(Guid nodeId, InputPacketBuffer buffer);