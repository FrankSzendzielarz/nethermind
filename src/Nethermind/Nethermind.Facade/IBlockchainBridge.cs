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

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Block = Nethermind.Core.Block;

namespace Nethermind.Facade
{
    public interface IBlockchainBridge
    {
        IReadOnlyCollection<Address> GetWalletAccounts();
        Signature Sign(Address address, Keccak message);
        
        void Sign(Transaction tx);

        int GetNetworkId();
        BlockHeader Head { get; }
        BlockHeader BestSuggested { get; }
        long BestKnown { get; }
        bool IsSyncing { get; }

        Block FindBlock(Keccak blockHash, bool mainChainOnly);
        Block FindBlock(long blockNumber);
        Block RetrieveHeadBlock();
        Block RetrieveGenesisBlock();

        (TxReceipt Receipt, Transaction Transaction) GetTransaction(Keccak transactionHash);
        Keccak GetBlockHash(Keccak transactionHash);
        Keccak SendTransaction(Transaction transaction);
        TxReceipt GetReceipt(Keccak txHash);
        BlockchainBridge.CallOutput Call(BlockHeader blockHeader, Transaction transaction);
        long EstimateGas(Block block, Transaction transaction);

        byte[] GetCode(Address address);
        byte[] GetCode(Keccak codeHash);
        BigInteger GetNonce(Address address);
        BigInteger GetBalance(Address address);
        byte[] GetStorage(Address address, BigInteger index);
        byte[] GetStorage(Address address, BigInteger index, Keccak storageRoot);
        
        Account GetAccount(Address address);
        Account GetAccount(Address address, Keccak stateRoot);

        int NewBlockFilter();
        int NewPendingTransactionFilter();

        int NewFilter(FilterBlock fromBlock, FilterBlock toBlock, object address = null,
            IEnumerable<object> topics = null);

        void UninstallFilter(int filterId);
        bool FilterExists(int filterId);
        FilterLog[] GetLogFilterChanges(int filterId);
        Keccak[] GetBlockFilterChanges(int filterId);
        Keccak[] GetPendingTransactionFilterChanges(int filterId);
        FilterType GetFilterType(int filterId);
        FilterLog[] GetFilterLogs(int filterId);

        FilterLog[] GetLogs(FilterBlock fromBlock, FilterBlock toBlock, object address = null,
            IEnumerable<object> topics = null);

        TxPoolInfo GetTxPoolInfo();
    }
}