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
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    [Todo(Improve.Refactor, "After the fast sync work there are some duplicated code parts for the 'by header' and 'by block' approaches.")]
    public class BlockTree : IBlockTree
    {
        private const int CacheSize = 64;
        private readonly LruCache<Keccak, Block> _blockCache = new LruCache<Keccak, Block>(CacheSize);
        private readonly LruCache<Keccak, BlockHeader> _headerCache = new LruCache<Keccak, BlockHeader>(CacheSize);
        private readonly LruCache<long, ChainLevelInfo> _blockInfoCache = new LruCache<long, ChainLevelInfo>(CacheSize);

        private const int MaxQueueSize = 10_000_000;

        public const int DbLoadBatchSize = 1000;

        private long _currentDbLoadBatchEnd;

        private ReaderWriterLockSlim _blockInfoLock = new ReaderWriterLockSlim();
        
        private object _batchInsertLock = new object();

        private readonly IDb _blockDb;

        private readonly IDb _headerDb;

        private ConcurrentDictionary<long, HashSet<Keccak>> _invalidBlocks = new ConcurrentDictionary<long, HashSet<Keccak>>();
        private readonly BlockDecoder _blockDecoder = new BlockDecoder();
        private readonly HeaderDecoder _headerDecoder = new HeaderDecoder();
        private readonly IDb _blockInfoDb;
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly ITxPool _txPool;
        private readonly ISyncConfig _syncConfig;

        public BlockHeader Genesis { get; private set; }
        public BlockHeader Head { get; private set; }
        public BlockHeader BestSuggestedHeader { get; private set; }
        public Block BestSuggestedBody { get; private set; }
        public BlockHeader LowestInsertedHeader { get; private set; }
        public Block LowestInsertedBody { get; private set; }
        public long BestKnownNumber { get; private set; }
        public int ChainId => _specProvider.ChainId;

        public bool CanAcceptNewBlocks { get; private set; } = true; // no need to sync it at the moment

        public BlockTree(
            IDb blockDb,
            IDb headerDb,
            IDb blockInfoDb,
            ISpecProvider specProvider,
            ITxPool txPool,
            ILogManager logManager)
            : this(blockDb, headerDb, blockInfoDb, specProvider, txPool, new SyncConfig(), logManager)
        {
        }

        public BlockTree(
            IDb blockDb,
            IDb headerDb,
            IDb blockInfoDb,
            ISpecProvider specProvider,
            ITxPool txPool,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockDb = blockDb ?? throw new ArgumentNullException(nameof(blockDb));
            _headerDb = headerDb ?? throw new ArgumentNullException(nameof(headerDb));
            _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));
            _specProvider = specProvider;
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));

            ChainLevelInfo genesisLevel = LoadLevel(0, true);
            if (genesisLevel != null)
            {
                if (genesisLevel.BlockInfos.Length != 1)
                {
                    // just for corrupted test bases
                    genesisLevel.BlockInfos = new[] {genesisLevel.BlockInfos[0]};
                    PersistLevel(0, genesisLevel);
                    //throw new InvalidOperationException($"Genesis level in DB has {genesisLevel.BlockInfos.Length} blocks");
                }

                LoadLowestInsertedHeader();
                LoadLowestInsertedBody();
                LoadBestKnown();

                if (genesisLevel.BlockInfos[0].WasProcessed)
                {
                    BlockHeader genesisHeader = LoadHeader(genesisLevel.BlockInfos[0].BlockHash).Header;
                    Genesis = genesisHeader;
                    LoadHeadBlock();
                }
            }

            if (_logger.IsInfo) _logger.Info($"Block tree initialized, last processed is {Head?.ToString(BlockHeader.Format.Short) ?? "0"}, best queued is {BestSuggestedHeader?.Number.ToString() ?? "0"}, best known is {BestKnownNumber}, lowest inserted header {LowestInsertedHeader?.Number}, body {LowestInsertedBody?.Number}");
        }

        private void LoadBestKnown()
        {
            long headNumber = Head?.Number ?? -1;
            long left = Math.Max(LowestInsertedHeader?.Number ?? 0, headNumber);
            long right = headNumber + MaxQueueSize;

            while (left != right)
            {
                long index = left + (right - left) / 2;
                ChainLevelInfo level = LoadLevel(index, true);
                if (level == null)
                {
                    right = index;
                }
                else
                {
                    left = index + 1;
                }
            }

            long result = left - 1;

            BestKnownNumber = result;

            if (BestKnownNumber < 0)
            {
                throw new InvalidOperationException($"Best known is {BestKnownNumber}");
            }
        }

        private void LoadLowestInsertedHeader()
        {
            long left = 0L;
            long right = LongConverter.FromString(_syncConfig.PivotNumber ?? "0x0");

            ChainLevelInfo lowestInsertedLevel = null;
            while (left != right)
            {
                if(_logger.IsTrace) _logger.Trace($"Finding lowest inserted header - L {left} | R {right}");
                long index = left + (right - left) / 2 + 1;
                ChainLevelInfo level = LoadLevel(index, true);
                if (level == null)
                {
                    left = index;
                }
                else
                {
                    lowestInsertedLevel = level;
                    right = index - 1L;
                }
            }
            
            if (lowestInsertedLevel == null)
            {
                if(_logger.IsTrace) _logger.Trace($"Lowest inserted header is null - L {left} | R {right}");
                LowestInsertedHeader = null;
            }
            else
            {
                BlockInfo blockInfo = lowestInsertedLevel.BlockInfos[0];
                LowestInsertedHeader = FindHeader(blockInfo.BlockHash);
                if(_logger.IsDebug) _logger.Debug($"Lowest inserted header is {LowestInsertedHeader?.ToString(BlockHeader.Format.Short)} {right} - L {left} | R {right}");                
            }
        }

        private void LoadLowestInsertedBody()
        {
            long left = 0L;
            long right = LongConverter.FromString(_syncConfig.PivotNumber ?? "0x0");

            Block lowestInsertedBlock = null;
            while (left != right)
            {
                if(_logger.IsDebug) _logger.Debug($"Finding lowest inserted body - L {left} | R {right}");
                long index = left + (right - left) / 2 + 1;
                ChainLevelInfo level = LoadLevel(index, true);
                Block block = level == null ? null : FindBlock(level.BlockInfos[0].BlockHash, false);
                if (block == null)
                {
                    left = index;
                }
                else
                {
                    lowestInsertedBlock = block;
                    right = index - 1;
                }
            }

            if (lowestInsertedBlock == null)
            {
                if(_logger.IsTrace) _logger.Trace($"Lowest inserted body is null - L {left} | R {right}");
                LowestInsertedBody = null;
            }
            else
            {
                if(_logger.IsDebug) _logger.Debug($"Lowest inserted body is {LowestInsertedBody?.ToString(Block.Format.Short)} {right} - L {left} | R {right}");
                LowestInsertedBody = lowestInsertedBlock;
            }
        }

        public async Task LoadBlocksFromDb(
            CancellationToken cancellationToken,
            long? startBlockNumber = null,
            int batchSize = DbLoadBatchSize,
            int maxBlocksToLoad = int.MaxValue)
        {
            try
            {
                CanAcceptNewBlocks = false;

                byte[] deletePointer = _blockInfoDb.Get(DeletePointerAddressInDb);
                if (deletePointer != null)
                {
                    Keccak deletePointerHash = new Keccak(deletePointer);
                    if (_logger.IsInfo) _logger.Info($"Cleaning invalid blocks starting from {deletePointer}");
                    CleanInvalidBlocks(deletePointerHash);
                }

                if (startBlockNumber == null)
                {
                    startBlockNumber = Head?.Number ?? 0;
                }
                else
                {
                    Head = startBlockNumber == 0 ? null : FindBlock(startBlockNumber.Value - 1)?.Header;
                }
                
                long blocksToLoad = Math.Min(FindNumberOfBlocksToLoadFromDb(), maxBlocksToLoad);
                if (blocksToLoad == 0)
                {
                    if (_logger.IsInfo) _logger.Info("Found no blocks to load from DB");
                    return;
                }

                if (_logger.IsInfo) _logger.Info($"Found {blocksToLoad} blocks to load from DB starting from current head block {Head?.ToString(BlockHeader.Format.Short)}");

                long blockNumber = startBlockNumber.Value;
                for (long i = 0; i < blocksToLoad; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    ChainLevelInfo level = LoadLevel(blockNumber);
                    if (level == null)
                    {
                        _logger.Warn($"Missing level - {blockNumber}");
                        break;
                    }

                    BigInteger maxDifficultySoFar = 0;
                    BlockInfo maxDifficultyBlock = null;
                    for (int blockIndex = 0; blockIndex < level.BlockInfos.Length; blockIndex++)
                    {
                        if (level.BlockInfos[blockIndex].TotalDifficulty > maxDifficultySoFar)
                        {
                            maxDifficultyBlock = level.BlockInfos[blockIndex];
                            maxDifficultySoFar = maxDifficultyBlock.TotalDifficulty;
                        }
                    }

                    level = null;
                    // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                    if (level != null)
                        // ReSharper disable once HeuristicUnreachableCode
                    {
                        // ReSharper disable once HeuristicUnreachableCode
                        throw new InvalidOperationException("just be aware that this level can be deleted by another thread after here");
                    }

                    if (maxDifficultyBlock == null)
                    {
                        throw new InvalidOperationException($"Expected at least one block at level {blockNumber}");
                    }

                    Block block = FindBlock(maxDifficultyBlock.BlockHash, false);
                    if (block == null)
                    {
                        BlockHeader header = FindHeader(maxDifficultyBlock.BlockHash, false);
                        if (header == null)
                        {
                            _blockInfoDb.Delete(blockNumber);
                            BestKnownNumber = blockNumber - 1;
                            // TODO: check if it is the last one
                            break;
                        }

                        BestSuggestedHeader = header;
                        if (i < blocksToLoad - 1024)
                        {
                            long jumpSize = blocksToLoad - 1024 - 1;
                            if (_logger.IsInfo) _logger.Info($"Switching to fast sync headers load - jumping from {i} to {i + jumpSize}.");
                            blockNumber += jumpSize;
                            i += jumpSize;
                        }

                        // copy paste from below less batching
                        if (i % batchSize == batchSize - 1 && i != blocksToLoad - 1 && Head.Number + batchSize < blockNumber)
                        {
                            if (_logger.IsInfo) _logger.Info($"Loaded {i + 1} out of {blocksToLoad} headers from DB.");
                        }
                    }
                    else
                    {
                        BestSuggestedHeader = block.Header;
                        BestSuggestedBody = block;
                        NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));

                        if (i % batchSize == batchSize - 1 && i != blocksToLoad - 1 && Head.Number + batchSize < blockNumber)
                        {
                            if (_logger.IsInfo)
                            {
                                _logger.Info($"Loaded {i + 1} out of {blocksToLoad} blocks from DB into processing queue, waiting for processor before loading more.");
                            }

                            _dbBatchProcessed = new TaskCompletionSource<object>();
                            using (cancellationToken.Register(() => _dbBatchProcessed.SetCanceled()))
                            {
                                _currentDbLoadBatchEnd = blockNumber - batchSize;
                                await _dbBatchProcessed.Task;
                            }
                        }
                    }

                    blockNumber++;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info($"Canceled loading blocks from DB at block {blockNumber}");
                }

                if (_logger.IsInfo)
                {
                    _logger.Info($"Completed loading blocks from DB at block {blockNumber} - best known {BestKnownNumber}");
                }
            }
            finally
            {
                CanAcceptNewBlocks = true;
            }
        }

        public AddBlockResult Insert(BlockHeader header)
        {
            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (header.Number == 0)
            {
                throw new InvalidOperationException("Genesis block should not be inserted.");
            }

            // validate hash here
            using (MemoryStream stream = Rlp.BorrowStream())
            {
                Rlp.Encode(stream, header);
                byte[] newRlp = stream.ToArray();

                _headerDb.Set(header.Hash, newRlp);
            }

            BlockInfo blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty ?? 0);

            try
            {
                _blockInfoLock.EnterWriteLock();
                ChainLevelInfo chainLevel = new ChainLevelInfo(false, new[] {blockInfo});
                PersistLevel(header.Number, chainLevel);
            }
            finally
            {
                _blockInfoLock.ExitWriteLock();
            }

            if (header.Number < (LowestInsertedHeader?.Number ?? long.MaxValue))
            {
                LowestInsertedHeader = header;
            }

            if (header.Number > BestKnownNumber)
            {
                BestKnownNumber = header.Number;
            }

            return AddBlockResult.Added;
        }

        public AddBlockResult Insert(Block block)
        {
            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (block.Number == 0)
            {
                throw new InvalidOperationException("Genesis block should not be inserted.");
            }
            
            using (MemoryStream stream = Rlp.BorrowStream())
            {
                Rlp.Encode(stream, block);
                byte[] newRlp = stream.ToArray();

                _blockDb.Set(block.Hash, newRlp);
            }

            long expectedNumber = (LowestInsertedBody?.Number - 1 ?? LongConverter.FromString(_syncConfig.PivotNumber ?? "0")); 
            if (block.Number != expectedNumber)
            {
                throw new InvalidOperationException($"Trying to insert out of order block {block.Number} when expected number was {expectedNumber}");
            }

            if (block.Number < (LowestInsertedBody?.Number ?? long.MaxValue))
            {
                LowestInsertedBody = block;
            }

            return AddBlockResult.Added;
        }

        public void Insert(IEnumerable<Block> blocks)
        {
            lock (_batchInsertLock)
            {
                try
                {
                    _blockDb.StartBatch();
                    foreach (Block block in blocks)
                    {
                        Insert(block);
                    }
                }
                finally
                {
                    _blockDb.CommitBatch();
                }
            }
        }

        private AddBlockResult Suggest(Block block, BlockHeader header, bool shouldProcess = true)
        {
#if DEBUG
            /* this is just to make sure that we do not fall into this trap when creating tests */
            if (header.StateRoot == null && !header.IsGenesis)
            {
                throw new InvalidDataException($"State root is null in {header.ToString(BlockHeader.Format.Short)}");
            }
#endif

            if (!CanAcceptNewBlocks)
            {
                return AddBlockResult.CannotAccept;
            }

            if (_invalidBlocks.ContainsKey(header.Number) && _invalidBlocks[header.Number].Contains(header.Hash))
            {
                return AddBlockResult.InvalidBlock;
            }

            bool isKnown = IsKnownBlock(header.Number, header.Hash);
            if (header.Number == 0)
            {
                if (BestSuggestedHeader != null)
                {
                    throw new InvalidOperationException("Genesis block should be added only once");
                }
            }
            else if (isKnown && (BestSuggestedHeader?.Number ?? 0) >= header.Number)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Block {header.Hash} already known.");
                }

                return AddBlockResult.AlreadyKnown;
            }
            else if (!IsKnownBlock(header.Number - 1, header.ParentHash))
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Could not find parent ({header.ParentHash}) of block {header.Hash}");
                }

                return AddBlockResult.UnknownParent;
            }

            SetTotalDifficulty(header);

            if (block != null && !isKnown)
            {
                using (MemoryStream stream = Rlp.BorrowStream())
                {
                    Rlp.Encode(stream, block);
                    byte[] newRlp = stream.ToArray();
                    _blockDb.Set(block.Hash, newRlp);
                }
            }

            if (!isKnown)
            {
                using (MemoryStream stream = Rlp.BorrowStream())
                {
                    Rlp.Encode(stream, header);
                    byte[] newRlp = stream.ToArray();
                    _headerDb.Set(header.Hash, newRlp);
                }

                BlockInfo blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty ?? 0);

                try
                {
                    _blockInfoLock.EnterWriteLock();
                    UpdateOrCreateLevel(header.Number, blockInfo);
                }
                finally
                {
                    _blockInfoLock.ExitWriteLock();
                }
            }

            if (header.IsGenesis || header.TotalDifficulty > (BestSuggestedHeader?.TotalDifficulty ?? 0))
            {
                if (header.IsGenesis)
                {
                    Genesis = header;
                }

                BestSuggestedHeader = header;
                if (block != null && shouldProcess)
                {
                    BestSuggestedBody = block;
                    NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));
                }
            }

            return AddBlockResult.Added;
        }

        public AddBlockResult SuggestHeader(BlockHeader header)
        {
            return Suggest(null, header);
        }

        public AddBlockResult SuggestBlock(Block block, bool shouldProcess = true)
        {
            return Suggest(block, block.Header, shouldProcess);
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            (Block block, BlockInfo _, ChainLevelInfo level) = Load(blockHash);
            if (block == null)
            {
                return null;
            }

            if (mainChainOnly)
            {
                bool isMain = level.HasBlockOnMainChain && level.BlockInfos[0].BlockHash.Equals(blockHash);
                return isMain ? block : null;
            }

            return block;
        }

        public BlockHeader FindHeader(Keccak blockHash, bool mainChainOnly)
        {
            (BlockHeader header, BlockInfo _, ChainLevelInfo level) = LoadHeader(blockHash);
            if (header == null)
            {
                return null;
            }

            if (mainChainOnly)
            {
                bool isMain = level.HasBlockOnMainChain && level.BlockInfos[0].BlockHash.Equals(blockHash);
                return isMain ? header : null;
            }

            return header;
        }

        public Block[] FindBlocks(Keccak blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            if (blockHash == null) throw new ArgumentNullException(nameof(blockHash));

            Block[] result = new Block[numberOfBlocks];
            Block startBlock = FindBlock(blockHash, true);
            if (startBlock == null)
            {
                return result;
            }

            for (int i = 0; i < numberOfBlocks; i++)
            {
                int blockNumber = (int) startBlock.Number + (reverse ? -1 : 1) * (i + i * skip);
                Block ithBlock = FindBlock(blockNumber);
                result[i] = ithBlock;
            }

            return result;
        }

        public BlockHeader[] FindHeaders(Keccak blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            if (blockHash == null) throw new ArgumentNullException(nameof(blockHash));

            BlockHeader[] result = new BlockHeader[numberOfBlocks];
            BlockHeader startBlock = FindHeader(blockHash);
            if (startBlock == null)
            {
                return result;
            }

            BlockHeader current = startBlock;
            int directionMultiplier = reverse ? -1 : 1;
            int responseIndex = 0;
            do
            {
                result[responseIndex] = current;
                responseIndex++;
                long nextNumber = startBlock.Number + directionMultiplier * (responseIndex * skip + responseIndex);
                if (nextNumber < 0)
                {
                    break;
                }

                current = FindHeader(nextNumber);
            } while (current != null && responseIndex < numberOfBlocks);

            return result;
        }

        private Keccak GetBlockHashOnMainOrOnlyHash(long blockNumber)
        {
            if (blockNumber < 0)
            {
                throw new ArgumentException($"{nameof(blockNumber)} must be greater or equal zero and is {blockNumber}",
                    nameof(blockNumber));
            }

            ChainLevelInfo level = LoadLevel(blockNumber);
            if (level == null)
            {
                return null;
            }

            if (level.HasBlockOnMainChain)
            {
                return level.BlockInfos[0].BlockHash;
            }

            if (level.BlockInfos.Length != 1)
            {
                if (_logger.IsError) _logger.Error($"Invalid request for block {blockNumber} ({level.BlockInfos.Length} blocks at the same level).");
                throw new InvalidOperationException($"Unexpected request by number for a block {blockNumber} that is not on the main chain and is not the only hash on chain");
            }

            return level.BlockInfos[0].BlockHash;
        }

        public Block FindBlock(long blockNumber)
        {
            Keccak hash = GetBlockHashOnMainOrOnlyHash(blockNumber);
            return Load(hash).Block;
        }

        public void DeleteInvalidBlock(Block invalidBlock)
        {
            if (_logger.IsDebug) _logger.Debug($"Deleting invalid block {invalidBlock.ToString(Block.Format.FullHashAndNumber)}");

            _invalidBlocks.AddOrUpdate(
                invalidBlock.Number,
                number => new HashSet<Keccak> {invalidBlock.Hash},
                (number, set) =>
                {
                    set.Add(invalidBlock.Hash);
                    return set;
                });

            BestSuggestedHeader = Head;
            BestSuggestedBody = Head == null ? null : FindBlock(Head.Hash, false);

            try
            {
                CanAcceptNewBlocks = false;
            }
            finally
            {
                CleanInvalidBlocks(invalidBlock.Hash);
                CanAcceptNewBlocks = true;
            }
        }

        private void CleanInvalidBlocks(Keccak deletePointer)
        {
            BlockHeader deleteHeader = FindHeader(deletePointer);
            long currentNumber = deleteHeader.Number;
            Keccak currentHash = deleteHeader.Hash;
            Keccak nextHash = null;
            ChainLevelInfo nextLevel = null;

            while (true)
            {
                ChainLevelInfo currentLevel = nextLevel ?? LoadLevel(currentNumber);
                nextLevel = LoadLevel(currentNumber + 1);

                bool shouldRemoveLevel = false;

                if (currentLevel != null) // preparing update of the level (removal of the invalid branch block)
                {
                    if (currentLevel.BlockInfos.Length == 1)
                    {
                        shouldRemoveLevel = true;
                    }
                    else
                    {
                        for (int i = 0; i < currentLevel.BlockInfos.Length; i++)
                        {
                            if (currentLevel.BlockInfos[0].BlockHash == currentHash)
                            {
                                currentLevel.BlockInfos = currentLevel.BlockInfos.Where(bi => bi.BlockHash != currentHash).ToArray();
                                break;
                            }
                        }
                    }
                }

                if (nextLevel != null) // just finding what the next descendant will be
                {
                    if (nextLevel.BlockInfos.Length == 1)
                    {
                        nextHash = nextLevel.BlockInfos[0].BlockHash;
                    }
                    else
                    {
                        for (int i = 0; i < nextLevel.BlockInfos.Length; i++)
                        {
                            BlockHeader potentialDescendant = FindHeader(nextLevel.BlockInfos[i].BlockHash);
                            if (potentialDescendant.ParentHash == currentHash)
                            {
                                nextHash = potentialDescendant.Hash;
                                break;
                            }
                        }
                    }

                    UpdateDeletePointer(nextHash);
                }
                else
                {
                    UpdateDeletePointer(null);
                }

                try
                {
                    _blockInfoLock.EnterWriteLock();
                    if (shouldRemoveLevel)
                    {
                        BestKnownNumber = Math.Min(BestKnownNumber, currentNumber - 1);
                        _blockInfoCache.Delete(currentNumber);
                        _blockInfoDb.Delete(currentNumber);
                    }
                    else
                    {
                        PersistLevel(currentNumber, currentLevel);
                    }
                }
                finally
                {
                    _blockInfoLock.ExitWriteLock();
                }

                if (_logger.IsInfo) _logger.Info($"Deleting invalid block {currentHash} at level {currentNumber}");
                _blockCache.Delete(currentHash);
                _blockDb.Delete(currentHash);
                _headerCache.Delete(currentHash);
                _headerDb.Delete(currentHash);

                if (nextHash == null)
                {
                    break;
                }

                currentNumber++;
                currentHash = nextHash;
                nextHash = null;
            }
        }

        public bool IsMainChain(Keccak blockHash)
        {
            long number = LoadNumberOnly(blockHash);
            ChainLevelInfo level = LoadLevel(number);
            return level.HasBlockOnMainChain && level.BlockInfos[0].BlockHash.Equals(blockHash);
        }

        public bool WasProcessed(long number, Keccak blockHash)
        {
            ChainLevelInfo levelInfo = LoadLevel(number);
            int? index = FindIndex(blockHash, levelInfo);
            if (index == null)
            {
                throw new InvalidOperationException($"Not able to find block {blockHash} index on the chain level");
            }

            return levelInfo.BlockInfos[index.Value].WasProcessed;
        }

        public void UpdateMainChain(Block[] processedBlocks)
        {
            if (processedBlocks.Length == 0)
            {
                return;
            }

            bool ascendingOrder = true;
            if (processedBlocks.Length > 1)
            {
                if (processedBlocks[processedBlocks.Length - 1].Number < processedBlocks[0].Number)
                {
                    ascendingOrder = false;
                }
            }

#if DEBUG
            for (int i = 0; i < processedBlocks.Length; i++)
            {
                if (i != 0)
                {
                    if (ascendingOrder && processedBlocks[i].Number != processedBlocks[i - 1].Number + 1)
                    {
                        throw new InvalidOperationException("Update main chain invoked with gaps");
                    }

                    if (!ascendingOrder && processedBlocks[i - 1].Number != processedBlocks[i].Number + 1)
                    {
                        throw new InvalidOperationException("Update main chain invoked with gaps");
                    }
                }
            }
#endif

            long lastNumber = ascendingOrder ? processedBlocks[processedBlocks.Length - 1].Number : processedBlocks[0].Number;
            long previousHeadNumber = Head?.Number ?? 0L;
            try
            {
                _blockInfoLock.EnterWriteLock();
                if (previousHeadNumber > lastNumber)
                {
                    for (long i = 0; i < previousHeadNumber - lastNumber; i++)
                    {
                        long levelNumber = previousHeadNumber - i;

                        ChainLevelInfo level = LoadLevel(levelNumber);
                        level.HasBlockOnMainChain = false;
                        PersistLevel(levelNumber, level);
                    }
                }

                for (int i = 0; i < processedBlocks.Length; i++)
                {
                    Block block = processedBlocks[i];
                    if (ShouldCache(block.Number))
                    {
                        _blockCache.Set(block.Hash, processedBlocks[i]);
                        _headerCache.Set(block.Hash, block.Header);
                    }

                    MoveToMain(processedBlocks[i]);
                }
            }
            finally
            {
                _blockInfoLock.ExitWriteLock();
            }
        }

        private TaskCompletionSource<object> _dbBatchProcessed;

        private void MoveToMain(Block block)
        {
            if (_logger.IsTrace) _logger.Trace($"Moving {block.ToString(Block.Format.Short)} to main");

            ChainLevelInfo level = LoadLevel(block.Number);
            int? index = FindIndex(block.Hash, level);
            if (index == null)
            {
                throw new InvalidOperationException($"Cannot move unknown block {block.ToString(Block.Format.FullHashAndNumber)} to main");
            }

            BlockInfo info = level.BlockInfos[index.Value];
            info.WasProcessed = true;
            if (index.Value != 0)
            {
                (level.BlockInfos[index.Value], level.BlockInfos[0]) = (level.BlockInfos[0], level.BlockInfos[index.Value]);
            }

            // tks: in testing chains we have a chain full of processed blocks that we process again
            //if (level.HasBlockOnMainChain)
            //{
            //    throw new InvalidOperationException("When moving to main encountered a block in main on the same level");
            //}

            level.HasBlockOnMainChain = true;
            PersistLevel(block.Number, level);

            BlockAddedToMain?.Invoke(this, new BlockEventArgs(block));

            if (block.IsGenesis || block.TotalDifficulty > (Head?.TotalDifficulty ?? 0))
            {
                if (block.Number == 0)
                {
                    Genesis = block.Header;
                }

                if (block.TotalDifficulty == null)
                {
                    throw new InvalidOperationException("Head block with null total difficulty");
                }

                UpdateHeadBlock(block);
            }

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                _txPool.RemoveTransaction(block.Transactions[i].Hash);
            }

            if (_logger.IsTrace) _logger.Trace($"Block {block.ToString(Block.Format.Short)} added to main chain");
        }

        [Todo(Improve.Refactor, "Look at this magic -1 behaviour, never liked it, now when it is split between BestKnownNumber and Head it is even worse")]
        private long FindNumberOfBlocksToLoadFromDb()
        {
            long headNumber = Head?.Number ?? -1;
            return BestKnownNumber - headNumber;
        }

        private void LoadHeadBlock()
        {
            byte[] data = _blockInfoDb.Get(HeadAddressInDb);
            if (data != null)
            {
                BlockHeader headBlockHeader = data.Length == 32
                    ? FindHeader(new Keccak(data), false)
                    : Rlp.Decode<BlockHeader>(data.AsRlpContext(), RlpBehaviors.AllowExtraData);

                ChainLevelInfo level = LoadLevel(headBlockHeader.Number);
                int? index = FindIndex(headBlockHeader.Hash, level);
                if (!index.HasValue)
                {
                    throw new InvalidDataException("Head block data missing from chain info");
                }

                headBlockHeader.TotalDifficulty = level.BlockInfos[index.Value].TotalDifficulty;

                Head = BestSuggestedHeader = headBlockHeader;
                BestSuggestedBody = FindBlock(headBlockHeader.Hash, false);
            }
        }

        public bool IsKnownBlock(long number, Keccak blockHash)
        {
            if (number > BestKnownNumber)
            {
                return false;
            }

            // IsKnownBlock will be mainly called when new blocks are incoming
            // and these are very likely to be all at the head of the chain
            if (blockHash == Head?.Hash)
            {
                return true;
            }

            if (_headerCache.Get(blockHash) != null)
            {
                return true;
            }

            ChainLevelInfo level = LoadLevel(number);
            return level != null && FindIndex(blockHash, level).HasValue;
        }

        internal static Keccak HeadAddressInDb = Keccak.Zero;
        internal static Keccak DeletePointerAddressInDb = new Keccak(new BitArray(32 * 8, true).ToBytes());

        private void UpdateDeletePointer(Keccak hash)
        {
            if (hash == null)
            {
                _blockInfoDb.Delete(DeletePointerAddressInDb);
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Deleting an invalid block or its descendant {hash}");
            _blockInfoDb.Set(DeletePointerAddressInDb, hash.Bytes);
        }

        private void UpdateHeadBlock(Block block)
        {
            if (block.IsGenesis)
            {
                Genesis = block.Header;
            }

            Head = block.Header;
            _blockInfoDb.Set(HeadAddressInDb, Head.Hash.Bytes);
            NewHeadBlock?.Invoke(this, new BlockEventArgs(block));
            if (_dbBatchProcessed != null)
            {
                if (block.Number == _currentDbLoadBatchEnd)
                {
                    TaskCompletionSource<object> completionSource = _dbBatchProcessed;
                    _dbBatchProcessed = null;
                    completionSource.SetResult(null);
                }
            }
        }

        private void UpdateOrCreateLevel(long number, BlockInfo blockInfo)
        {
            ChainLevelInfo level = LoadLevel(number, false);

            if (level != null)
            {
                BlockInfo[] blockInfos = new BlockInfo[level.BlockInfos.Length + 1];
                for (int i = 0; i < level.BlockInfos.Length; i++)
                {
                    blockInfos[i] = level.BlockInfos[i];
                }

                blockInfos[blockInfos.Length - 1] = blockInfo;
                level.BlockInfos = blockInfos;
            }
            else
            {
                if (number > BestKnownNumber)
                {
                    BestKnownNumber = number;
                }

                level = new ChainLevelInfo(false, new[] {blockInfo});
            }

            PersistLevel(number, level);
        }

        /* error-prone: all methods that load a level, change it and then persist need to execute everything under a lock */
        private void PersistLevel(long number, ChainLevelInfo level)
        {
//            _blockInfoCache.Set(number, level);
            _blockInfoDb.Set(number, Rlp.Encode(level).Bytes);
        }

        private (BlockInfo Info, ChainLevelInfo Level) LoadInfo(long number, Keccak blockHash)
        {
            ChainLevelInfo chainLevelInfo = LoadLevel(number);
            if (chainLevelInfo == null)
            {
                return (null, null);
            }

            int? index = FindIndex(blockHash, chainLevelInfo);
            return index.HasValue ? (chainLevelInfo.BlockInfos[index.Value], chainLevelInfo) : (null, chainLevelInfo);
        }

        private int? FindIndex(Keccak blockHash, ChainLevelInfo level)
        {
            for (int i = 0; i < level.BlockInfos.Length; i++)
            {
                if (level.BlockInfos[i].BlockHash.Equals(blockHash))
                {
                    return i;
                }
            }

            return null;
        }

        private ChainLevelInfo LoadLevel(long number, bool forceLoad = true)
        {
            if (number > BestKnownNumber && !forceLoad)
            {
                return null;
            }

            ChainLevelInfo chainLevelInfo = _blockInfoCache.Get(number);
            if (chainLevelInfo == null)
            {
                byte[] levelBytes = _blockInfoDb.Get(number);
                if (levelBytes == null)
                {
                    return null;
                }

                chainLevelInfo = Rlp.Decode<ChainLevelInfo>(new Rlp(levelBytes));
            }

            return chainLevelInfo;
        }

        private long LoadNumberOnly(Keccak blockHash)
        {
            BlockHeader header = _headerCache.Get(blockHash);
            if (header != null)
            {
                return header.Number;
            }

            byte[] headerData = _headerDb.Get(blockHash);
            if (headerData == null)
            {
                throw new InvalidOperationException(
                    $"Not able to retrieve block number for an unknown block {blockHash}");
            }

            header = _headerDecoder.Decode(headerData.AsRlpContext(), RlpBehaviors.AllowExtraData);
            _headerCache.Set(blockHash, header);
            return header.Number;
        }

        public BlockHeader FindHeader(Keccak blockHash)
        {
            BlockHeader header = _headerCache.Get(blockHash);
            if (header == null)
            {
                byte[] data = _headerDb.Get(blockHash);
                if (data == null)
                {
                    return null;
                }

                header = _headerDecoder.Decode(data.AsRlpContext(), RlpBehaviors.AllowExtraData);
                _headerCache.Set(blockHash, header);
            }

            BlockInfo blockInfo = LoadInfo(header.Number, header.Hash).Info;
            header.TotalDifficulty = blockInfo.TotalDifficulty;
            return header;
        }

        public BlockHeader FindHeader(long number)
        {
            Keccak hash = GetBlockHashOnMainOrOnlyHash(number);
            return hash == null ? null : FindHeader(hash);
        }

        private (BlockHeader Header, BlockInfo BlockInfo, ChainLevelInfo Level) LoadHeader(Keccak blockHash)
        {
            if (blockHash == null || blockHash == Keccak.Zero)
            {
                return (null, null, null);
            }

            BlockHeader header = _headerCache.Get(blockHash);
            if (header == null)
            {
                byte[] data = _headerDb.Get(blockHash);
                if (data == null)
                {
                    return (null, null, null);
                }

                header = _headerDecoder.Decode(data.AsRlpContext(), RlpBehaviors.AllowExtraData);
                if (ShouldCache(header.Number))
                {
                    _headerCache.Set(blockHash, header);
                }
            }

            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(header.Number, header.Hash);
            if (level == null || blockInfo == null)
            {
                // TODO: this is here because storing block data is not transactional
                // TODO: would be great to remove it, he?
                SetTotalDifficulty(header);
                blockInfo = new BlockInfo(header.Hash, header.TotalDifficulty.Value);
                try
                {
                    _blockInfoLock.EnterWriteLock();
                    UpdateOrCreateLevel(header.Number, blockInfo);
                }
                finally
                {
                    _blockInfoLock.ExitWriteLock();
                }

                (blockInfo, level) = LoadInfo(header.Number, header.Hash);
            }
            else
            {
                header.TotalDifficulty = blockInfo.TotalDifficulty;
            }

            return (header, blockInfo, level);
        }

        /// <summary>
        /// To make cache useful even when we handle sync requests
        /// </summary>
        /// <param name="number"></param>
        /// <returns></returns>
        private bool ShouldCache(long number)
        {
            return number == 0L || Head == null || number > Head.Number - CacheSize && number <= Head.Number + 1;
        }

        private (Block Block, BlockInfo Info, ChainLevelInfo Level) Load(Keccak blockHash)
        {
            if (blockHash == null || blockHash == Keccak.Zero)
            {
                return (null, null, null);
            }

            Block block = _blockCache.Get(blockHash);
            if (block == null)
            {
                byte[] data = _blockDb.Get(blockHash);
                if (data == null)
                {
                    return (null, null, null);
                }

                block = _blockDecoder.Decode(data.AsRlpContext(), RlpBehaviors.AllowExtraData);
                if (ShouldCache(block.Number))
                {
                    _blockCache.Set(blockHash, block);
                    _headerCache.Set(blockHash, block.Header);
                }
            }

            (BlockInfo blockInfo, ChainLevelInfo level) = LoadInfo(block.Number, block.Hash);
            if (level == null || blockInfo == null)
            {
                // TODO: this is here because storing block data is not transactional
                // TODO: would be great to remove it, he?
                SetTotalDifficulty(block.Header);
                blockInfo = new BlockInfo(block.Hash, block.TotalDifficulty.Value);
                try
                {
                    _blockInfoLock.EnterWriteLock();
                    UpdateOrCreateLevel(block.Number, blockInfo);
                }
                finally
                {
                    _blockInfoLock.ExitWriteLock();
                }

                (blockInfo, level) = LoadInfo(block.Number, block.Hash);
            }
            else
            {
                block.Header.TotalDifficulty = blockInfo.TotalDifficulty;
            }

            return (block, blockInfo, level);
        }

        private void SetTotalDifficulty(BlockHeader header)
        {
            if (_logger.IsTrace)
            {
                _logger.Trace($"Calculating total difficulty for {header}");
            }

            if (header.Number == 0)
            {
                header.TotalDifficulty = header.Difficulty;
            }
            else
            {
                BlockHeader parentHeader = this.FindParentHeader(header);
                if (parentHeader == null)
                {
                    throw new InvalidOperationException($"An orphaned block on the chain {header}");
                }

                if (parentHeader.TotalDifficulty == null)
                {
                    throw new InvalidOperationException(
                        $"Parent's {nameof(parentHeader.TotalDifficulty)} unknown when calculating for {header}");
                }

                header.TotalDifficulty = parentHeader.TotalDifficulty + header.Difficulty;
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"Calculated total difficulty for {header} is {header.TotalDifficulty}");
            }
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain;

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock;

        public event EventHandler<BlockEventArgs> NewHeadBlock;
    }
}