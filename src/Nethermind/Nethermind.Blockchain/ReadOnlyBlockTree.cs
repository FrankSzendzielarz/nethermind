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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public class ReadOnlyBlockTree : IBlockTree
    {
        private readonly IBlockTree _wrapped;

        public ReadOnlyBlockTree(IBlockTree wrapped)
        {
            _wrapped = wrapped;
        }

        public int ChainId => _wrapped.ChainId;
        public BlockHeader Genesis => _wrapped.Genesis;
        public BlockHeader BestSuggestedHeader => _wrapped.BestSuggestedHeader;
        public BlockHeader LowestInsertedHeader => _wrapped.LowestInsertedHeader;
        public Block LowestInsertedBody => _wrapped.LowestInsertedBody;
        public Block BestSuggestedBody => _wrapped.BestSuggestedBody;
        public long BestKnownNumber => _wrapped.BestKnownNumber;
        public BlockHeader Head => _wrapped.Head;
        public bool CanAcceptNewBlocks { get; } = false;

        public Task LoadBlocksFromDb(CancellationToken cancellationToken, long? startBlockNumber, int batchSize = BlockTree.DbLoadBatchSize, int maxBlocksToLoad = Int32.MaxValue)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(LoadBlocksFromDb)} calls");
        }

        public AddBlockResult Insert(Block block)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");
        }

        public void Insert(IEnumerable<Block> blocks)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");
        }

        public AddBlockResult SuggestBlock(Block block, bool shouldProcess = true)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestBlock)} calls");
        }

        public AddBlockResult Insert(BlockHeader header)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(Insert)} calls");
        }

        public AddBlockResult SuggestHeader(BlockHeader header)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestHeader)} calls");
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            return _wrapped.FindBlock(blockHash, mainChainOnly);
        }

        public BlockHeader FindHeader(Keccak blockHash, bool mainChainOnly)
        {
            return _wrapped.FindHeader(blockHash, mainChainOnly);
        }

        public BlockHeader FindHeader(Keccak blockHash)
        {
            return _wrapped.FindHeader(blockHash);
        }

        public BlockHeader FindHeader(long blockNumber)
        {
            return _wrapped.FindHeader(blockNumber);
        }

        public Block[] FindBlocks(Keccak blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            return _wrapped.FindBlocks(blockHash, numberOfBlocks, skip, reverse);
        }

        public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            return _wrapped.FindHeaders(hash, numberOfBlocks, skip, reverse);
        }

        public Block FindBlock(long blockNumber)
        {
            return _wrapped.FindBlock(blockNumber);
        }

        public void DeleteInvalidBlock(Block invalidBlock)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(DeleteInvalidBlock)} calls");
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _wrapped.IsMainChain(blockHash);
        }

        public bool IsKnownBlock(long number, Keccak blockHash)
        {
            return _wrapped.IsKnownBlock(number, blockHash);
        }

        public bool WasProcessed(long number, Keccak blockHash)
        {
            return _wrapped.WasProcessed(number, blockHash);
        }

        public void MoveToMain(Block block)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MoveToMain)} calls");
        }

        public void MoveToMain(Keccak blockHash)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MoveToMain)} calls");
        }

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockEventArgs> NewHeadBlock
        {
            add { }
            remove { }
        }

        public void UpdateMainChain(Block[] processedBlocks)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(UpdateMainChain)} calls");
        }
    }
}