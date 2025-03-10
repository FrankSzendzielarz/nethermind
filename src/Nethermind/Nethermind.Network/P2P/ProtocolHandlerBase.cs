﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public abstract class ProtocolHandlerBase
    {
        protected INodeStatsManager StatsManager { get; }
        private readonly IMessageSerializationService _serializer;
        protected ISession Session { get; }
        private readonly TaskCompletionSource<MessageBase> _initCompletionSource;

        protected ProtocolHandlerBase(ISession session, INodeStatsManager nodeStats, IMessageSerializationService serializer, ILogManager logManager)
        {
            Logger = logManager?.GetClassLogger(GetType()) ?? throw new ArgumentNullException(nameof(logManager));
            StatsManager = nodeStats ?? throw new ArgumentNullException(nameof(nodeStats));
            Session = session ?? throw new ArgumentNullException(nameof(session));
            
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _initCompletionSource = new TaskCompletionSource<MessageBase>();
        }

        protected ILogger Logger { get; }
        
        protected abstract TimeSpan InitTimeout { get; }

        protected T Deserialize<T>(byte[] data) where T : P2PMessage
        {
            return _serializer.Deserialize<T>(data);
        }

        protected void Send<T>(T message) where T : P2PMessage
        {
            if (Logger.IsTrace) Logger.Trace($"Sending {typeof(T).Name}");
            Packet packet = new Packet(message.Protocol, message.PacketType, _serializer.Serialize(message));
            Session.DeliverMessage(packet);   
        }

        protected async Task CheckProtocolInitTimeout()
        {
            var receivedInitMsgTask = _initCompletionSource.Task;
            var firstTask = await Task.WhenAny(receivedInitMsgTask, Task.Delay(InitTimeout));
            
            if (firstTask != receivedInitMsgTask)
            {
                if (Logger.IsTrace)
                {
                    Logger.Trace($"Disconnecting due to timeout for protocol init message ({GetType().Name}): {Session.RemoteNodeId}");
                }
                
                Session.InitiateDisconnect(DisconnectReason.ReceiveMessageTimeout, "protocol init timeout");
            }
        }

        protected void ReceivedProtocolInitMsg(MessageBase msg)
        {
            _initCompletionSource?.SetResult(msg);
        }
    }
}