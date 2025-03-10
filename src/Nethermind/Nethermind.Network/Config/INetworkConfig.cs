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

using Nethermind.Config;

namespace Nethermind.Network.Config
{
    public interface INetworkConfig : IConfig
    {
        [ConfigItem(Description = "Currently ignored.", DefaultValue = "null")]
        string TrustedPeers { get; set; }
        
        [ConfigItem(Description = "List of nodes for which we will keep the connection on. Static nodes are not counted to the max number of nodes limit.", DefaultValue = "null")]
        string StaticPeers { get; set; }
        
        [ConfigItem(Description = "If 'false' then discovered node list will be cleared on each restart.", DefaultValue = "true")]
        bool IsPeersPersistenceOn { get; set; }

        [ConfigItem(Description = "Max number of connected peers.", DefaultValue = "25")]
        int ActivePeersMaxCount { get; }

        [ConfigItem(DefaultValue = "5000")]
        int PeersPersistenceInterval { get; set; }
        
        [ConfigItem(DefaultValue = "100")]
        int PeersUpdateInterval { get; set; }

        [ConfigItem(DefaultValue = "10000")]
        int P2PPingInterval { get; }

        [ConfigItem(DefaultValue = "3")]
        int P2PPingRetryCount { get; }

        [ConfigItem(DefaultValue = "2000")]
        int MaxPersistedPeerCount { get; }
        
        [ConfigItem(DefaultValue = "2200")]
        int PersistedPeerCountCleanupThreshold { get; set; }
        
        [ConfigItem(DefaultValue = "10000")]
        int MaxCandidatePeerCount { get; set; }
        
        [ConfigItem(DefaultValue = "11000")]
        int CandidatePeerCountCleanupThreshold { get; set; }
    }
}