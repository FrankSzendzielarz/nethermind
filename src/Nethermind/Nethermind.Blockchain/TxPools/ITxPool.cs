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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TxPools
{
    public interface ITxPool
    {
        Transaction[] GetPendingTransactions();
        void AddFilter<T>(T filter) where T : ITxFilter;
        void AddPeer(ISyncPeer peer);
        void RemovePeer(PublicKey nodeId);
        AddTxResult AddTransaction(Transaction transaction, long blockNumber);
        void RemoveTransaction(Keccak hash);
        bool TryGetSender(Keccak hash, out Address sender);
        event EventHandler<TxEventArgs> NewPending;
        event EventHandler<TxEventArgs> RemovedPending;
    }
}