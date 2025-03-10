/*
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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Blockchain.Synchronization
{
    internal class SyncPeersReport
    {
        private TimeSpan _fullPeerListInterval = TimeSpan.FromSeconds(120);
        private DateTime _timeOfTheLastFullPeerListLogEntry = DateTime.MinValue;
        private int _currentInitializedPeerCount;

        private readonly IEthSyncPeerPool _peerPool;
        private readonly INodeStatsManager _stats;
        private readonly ILogger _logger;

        public SyncPeersReport(IEthSyncPeerPool peerPool, INodeStatsManager statsManager, ILogManager logManager)
        {
            _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _stats = statsManager ?? throw new ArgumentNullException(nameof(statsManager));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private object _writeLock = new object();

        public void Write()
        {
            TimeSpan timeSinceLastEntry;
            bool initializedCountChanged;
            lock (_writeLock)
            {
                timeSinceLastEntry = DateTime.UtcNow - _timeOfTheLastFullPeerListLogEntry;
                 int initializedPeerCount = _peerPool.AllPeers.Count(p => p.IsInitialized);
                 initializedCountChanged = initializedPeerCount != _currentInitializedPeerCount;
                _currentInitializedPeerCount = initializedPeerCount;
            }

            List<PeerInfo> displayedPeers = new List<PeerInfo>();
            if (timeSinceLastEntry > _fullPeerListInterval)
            {
                _timeOfTheLastFullPeerListLogEntry = DateTime.UtcNow;
                if (_logger.IsInfo) _logger.Info($"Sync peers - Initialized: {_currentInitializedPeerCount} | All: {_peerPool.PeerCount} | Max: {_peerPool.PeerMaxCount}");
                foreach (PeerInfo peerInfo in _peerPool.AllPeers)
                {
                    displayedPeers.Add(peerInfo);
                }
            }
            else if (initializedCountChanged)
            {
                if (_logger.IsInfo) _logger.Info($"Sync peers {_currentInitializedPeerCount}({_peerPool.PeerCount})/{_peerPool.PeerMaxCount}");
                foreach (PeerInfo peerInfo in _peerPool.AllPeers)
                {
                    if (peerInfo.IsAllocated)
                    {
                        displayedPeers.Add(peerInfo);
                    }
                }
            }
            else if (_currentInitializedPeerCount == 0)
            {
                if (_logger.IsInfo) _logger.Info($"Sync peers 0({_peerPool.PeerCount})/{_peerPool.PeerMaxCount}, searching for peers to sync with...");
            }

            foreach (PeerInfo peerInfo in displayedPeers.Where(pi => pi != null).OrderBy(p => p.SyncPeer?.Node?.Host))
            {
                string prefix = peerInfo.IsAllocated ? " * " : "   ";
                if (_logger.IsInfo) _logger.Info($"{prefix}{peerInfo}[{_stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000}]");
            }
        }
    }
}