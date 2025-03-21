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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Timers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Model;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.Blockchain.TxPools
{
    public class TxPool : ITxPool
    {
        private static int _seed = Environment.TickCount;

        private static readonly ThreadLocal<Random> Random =
            new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref _seed)));

        private readonly ConcurrentDictionary<Keccak, Transaction> _pendingTransactions =
            new ConcurrentDictionary<Keccak, Transaction>();

        private readonly ConcurrentDictionary<Type, ITxFilter> _filters =
            new ConcurrentDictionary<Type, ITxFilter>();

        private readonly ITxStorage _txStorage;
        private readonly IPendingTxThresholdValidator _pendingTxThresholdValidator;
        private readonly ITimestamp _timestamp;

        private readonly ConcurrentDictionary<PublicKey, ISyncPeer> _peers =
            new ConcurrentDictionary<PublicKey, ISyncPeer>();

        private readonly IEthereumEcdsa _ecdsa;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        private readonly int _peerNotificationThreshold;

        public TxPool(ITxStorage txStorage,
            IPendingTxThresholdValidator pendingTxThresholdValidator,
            ITimestamp timestamp, IEthereumEcdsa ecdsa, ISpecProvider specProvider, ILogManager logManager,
            int removePendingTransactionInterval = 600,
            int peerNotificationThreshold = 20)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txStorage = txStorage ?? throw new ArgumentNullException(nameof(txStorage));
            _pendingTxThresholdValidator = pendingTxThresholdValidator;
            _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _peerNotificationThreshold = peerNotificationThreshold;
            if (removePendingTransactionInterval <= 0)
            {
                return;
            }

            var timer = new Timer(removePendingTransactionInterval * 1000);
            timer.Elapsed += OnTimerElapsed;
            timer.Start();

            _ownTimer = new Timer(500);
            _ownTimer.Elapsed += OwnTimerOnElapsed;
            _ownTimer.AutoReset = false;
            _ownTimer.Start();
        }

        private System.Timers.Timer _ownTimer;

        private void OwnTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_ownTransactions.Count > 0)
            {
                foreach ((_, Transaction tx) in _ownTransactions)
                {
                    NotifyAllPeers(tx);
                }

                _ownTimer.Enabled = true;
            }
        }

        public Transaction[] GetPendingTransactions() => _pendingTransactions.Values.ToArray();

        public void AddFilter<T>(T filter) where T : ITxFilter
            => _filters.TryAdd(filter.GetType(), filter);

        public void AddPeer(ISyncPeer peer)
        {
            if (!_peers.TryAdd(peer.Node.Id, peer))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Added a peer: {peer.ClientId}");
        }

        public void RemovePeer(PublicKey nodeId)
        {
            if (!_peers.TryRemove(nodeId, out _))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Removed a peer: {nodeId}");
        }

        public AddTxResult AddTransaction(Transaction transaction, long blockNumber)
        {
            Metrics.PendingTransactionsReceived++;

            // beware we are discarding here the old signature scheme without ChainId
            if (transaction.Signature.GetChainId == null)
            {
                Metrics.PendingTransactionsDiscarded++;
                return AddTxResult.OldScheme;
            }

            if (transaction.Signature.GetChainId != _specProvider.ChainId)
            {
                Metrics.PendingTransactionsDiscarded++;
                return AddTxResult.InvalidChainId;
            }

            if (!_pendingTransactions.TryAdd(transaction.Hash, transaction))
            {
                Metrics.PendingTransactionsKnown++;
                return AddTxResult.AlreadyKnown;
            }

            if (_txStorage.Get(transaction.Hash) != null)
            {
                Metrics.PendingTransactionsKnown++;
                return AddTxResult.AlreadyKnown;
            }

            transaction.SenderAddress = _ecdsa.RecoverAddress(transaction, blockNumber);

            // check nonce

            if (transaction.DeliveredBy == null)
            {
                _ownTransactions.TryAdd(transaction.Hash, transaction);
                _ownTimer.Enabled = true;

                if (_logger.IsInfo) _logger.Info($"Broadcasting own transaction {transaction.Hash} to {_peers.Count} peers");
            }
            
            NotifySelectedPeers(transaction);

            FilterAndStoreTransaction(transaction, blockNumber);
            NewPending?.Invoke(this, new TxEventArgs(transaction));
            return AddTxResult.Added;
        }

        private void FilterAndStoreTransaction(Transaction transaction, long blockNumber)
        {
            var filters = _filters.Values;
            if (filters.Any(filter => !filter.IsValid(transaction)))
            {
                return;
            }

            _txStorage.Add(transaction, blockNumber);
            if (_logger.IsTrace) _logger.Trace($"Added a transaction: {transaction.Hash}");
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs eventArgs)
        {
            if (_pendingTransactions.Count == 0)
            {
                return;
            }

            var hashes = new List<Keccak>();
            var timestamp = new UInt256(_timestamp.EpochSeconds);
            foreach (var transaction in _pendingTransactions.Values)
            {
                if (_pendingTxThresholdValidator.IsRemovable(timestamp, transaction.Timestamp))
                {
                    hashes.Add(transaction.Hash);
                }
            }

            for (var i = 0; i < hashes.Count; i++)
            {
                if (_pendingTransactions.TryRemove(hashes[i], out var transaction))
                {
                    RemovedPending?.Invoke(this, new TxEventArgs(transaction));
                }
            }
        }

        public void RemoveTransaction(Keccak hash)
        {
            if (_pendingTransactions.TryRemove(hash, out var transaction))
            {
                RemovedPending?.Invoke(this, new TxEventArgs(transaction));
            }

            if (_ownTransactions.Count != 0)
            {
                bool ownIncluded = _ownTransactions.TryRemove(hash, out _);
                if (ownIncluded)
                {
                    if (_logger.IsInfo) _logger.Trace($"Transaction {hash} created on this node was included in the block");
                }
            }

            _txStorage.Delete(hash);
            if (_logger.IsTrace) _logger.Trace($"Deleted a transaction: {hash}");
        }

        public bool TryGetSender(Keccak hash, out Address sender)
        {
            bool found = _pendingTransactions.TryGetValue(hash, out Transaction transaction);
            sender = found ? transaction.SenderAddress : null;
            return found;
        }

        public event EventHandler<TxEventArgs> NewPending;
        public event EventHandler<TxEventArgs> RemovedPending;

        private void Notify(ISyncPeer peer, Transaction transaction)
        {
            var timestamp = new UInt256(_timestamp.EpochSeconds);
            if (_pendingTxThresholdValidator.IsObsolete(timestamp, transaction.Timestamp))
            {
                return;
            }

            Metrics.PendingTransactionsSent++;
            peer.SendNewTransaction(transaction);

            if (_logger.IsTrace) _logger.Trace($"Notified {peer.Node.Id} about a transaction: {transaction.Hash}");
        }

        private ConcurrentDictionary<Keccak, Transaction> _ownTransactions = new ConcurrentDictionary<Keccak, Transaction>();

        private void NotifyAllPeers(Transaction transaction)
        {
            foreach ((_, ISyncPeer peer) in _peers)
            {
                Notify(peer, transaction);
            }
        }
        
        private void NotifySelectedPeers(Transaction transaction)
        {
            foreach ((_, ISyncPeer peer) in _peers)
            {
                if (transaction.DeliveredBy == null)
                {
                    Notify(peer, transaction);
                    continue;
                }
                
                if (transaction.DeliveredBy.Equals(peer.Node.Id))
                {
                    continue;
                }

                if (_peerNotificationThreshold < Random.Value.Next(1, 100))
                {
                    continue;
                }

                Notify(peer, transaction);
            }
        }
    }
}