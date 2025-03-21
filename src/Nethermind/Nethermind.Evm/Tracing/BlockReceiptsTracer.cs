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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm.Tracing
{
    public class BlockReceiptsTracer : IBlockTracer, ITxTracer
    {
        private Block _block;
        private readonly ISpecProvider _specProvider;
        private readonly IStateProvider _stateProvider;
        public bool IsTracingReceipt => true;
        public bool IsTracingCalls => _currentTxTracer.IsTracingCalls;
        public bool IsTracingOpLevelStorage => _currentTxTracer.IsTracingOpLevelStorage;
        public bool IsTracingMemory => _currentTxTracer.IsTracingMemory;
        public bool IsTracingInstructions => _currentTxTracer.IsTracingInstructions;
        public bool IsTracingStack => _currentTxTracer.IsTracingStack;
        public bool IsTracingState => _currentTxTracer.IsTracingState;

        private IBlockTracer _otherTracer;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs)
        {
            TxReceipts[_currentIndex] = BuildReceipt(recipient, gasSpent, StatusCode.Success, logs);
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs);
            }
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error)
        {
            TxReceipts[_currentIndex] = BuildFailedReceipt(recipient, gasSpent, error);
            if (_currentTxTracer.IsTracingReceipt)
            {
                _currentTxTracer.MarkAsFailed(recipient, gasSpent, output, error);
            }
        }

        private TxReceipt BuildFailedReceipt(Address recipient, long gasSpent, string error)
        {
            TxReceipt receipt = BuildReceipt(recipient, gasSpent, StatusCode.Failure, LogEntry.EmptyLogs);
            receipt.Error = error;
            return receipt;
        }

        private TxReceipt BuildReceipt(Address recipient, long spentGas, byte statusCode, LogEntry[] logEntries)
        {
            Transaction transaction = _block.Transactions[_currentIndex];
            TxReceipt txReceipt = new TxReceipt();
            txReceipt.Logs = logEntries;
            txReceipt.Bloom = logEntries.Length == 0 ? Bloom.Empty : new Bloom(logEntries);
            txReceipt.GasUsedTotal = _block.GasUsed;
            if (!_specProvider.GetSpec(_block.Number).IsEip658Enabled)
            {
                txReceipt.PostTransactionState = _stateProvider.StateRoot;
            }

            txReceipt.StatusCode = statusCode;
            txReceipt.Recipient = transaction.IsContractCreation ? null : recipient;

            txReceipt.BlockHash = _block.Hash;
            txReceipt.BlockNumber = _block.Number;
            txReceipt.Index = _currentIndex;
            txReceipt.GasUsed = spentGas;
            txReceipt.Sender = transaction.SenderAddress;
            txReceipt.ContractAddress = transaction.IsContractCreation ? recipient : null;
            txReceipt.TransactionHash = transaction.Hash;

            return txReceipt;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            _currentTxTracer.StartOperation(depth, gas, opcode, pc);
        }

        public void SetOperationError(string error)
        {
            _currentTxTracer.SetOperationError(error);
        }

        public void SetOperationRemainingGas(long gas)
        {
            _currentTxTracer.SetOperationRemainingGas(gas);
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _currentTxTracer.SetOperationMemorySize(newSize);
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            _currentTxTracer.SetOperationStorage(address, storageIndex, newValue, currentValue);
        }

        public void ReportBalanceChange(Address address, UInt256 before, UInt256 after)
        {
            _currentTxTracer.ReportBalanceChange(address, before, after);
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            _currentTxTracer.ReportCodeChange(address, before, after);
        }

        public void ReportNonceChange(Address address, UInt256 before, UInt256 after)
        {
            _currentTxTracer.ReportNonceChange(address, before, after);
        }

        public void ReportStorageChange(StorageAddress storageAddress, byte[] before, byte[] after)
        {
            _currentTxTracer.ReportStorageChange(storageAddress, before, after);
        }

        public void ReportCall(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType)
        {
            _currentTxTracer.ReportCall(gas, value, @from, to, input, callType);
        }

        public void ReportCallEnd(long gas, byte[] output)
        {
            _currentTxTracer.ReportCallEnd(gas, output);
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            _currentTxTracer.SetOperationStack(stackTrace);
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            _currentTxTracer.SetOperationMemory(memoryTrace);
        }

        private ITxTracer _currentTxTracer;
        private int _currentIndex;
        public TxReceipt[] TxReceipts { get; private set; }

        public bool IsTracingRewards => _otherTracer.IsTracingRewards;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            _otherTracer.ReportReward(author, rewardType, rewardValue);
        }

        public void StartNewBlockTrace(Block block)
        {
            if (_otherTracer == null)
            {
                throw new InvalidOperationException("other tracer not set in receipts tracer");
            }
            
            _block = block;
            _currentIndex = 0;
            TxReceipts = new TxReceipt[_block.Transactions.Length];
            _otherTracer.StartNewBlockTrace(block);
        }

        public ITxTracer StartNewTxTrace(Keccak txHash)
        {
            _currentTxTracer = _otherTracer.StartNewTxTrace(txHash);
            return _currentTxTracer;
        }

        public void EndTxTrace()
        {
            _otherTracer.EndTxTrace();
            _currentIndex++;
        }

        public BlockReceiptsTracer(ISpecProvider specProvider, IStateProvider stateProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        }

        public void SetOtherTracer(IBlockTracer blockTracer)
        {
            _otherTracer = blockTracer;
        }
    }
}