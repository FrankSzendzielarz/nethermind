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

using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Encoding
{
    public class ReceiptDecoder : IRlpDecoder<TxReceipt>
    {
        public TxReceipt Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            TxReceipt txReceipt = new TxReceipt();
            context.ReadSequenceLength();
            byte[] firstItem = context.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            if(isStorage) txReceipt.BlockHash = context.DecodeKeccak();
            if(isStorage) txReceipt.BlockNumber = (long)context.DecodeUInt256();
            if(isStorage) txReceipt.Index = context.DecodeInt();
            if(isStorage) txReceipt.Sender = context.DecodeAddress();
            if(isStorage) txReceipt.Recipient = context.DecodeAddress();
            if(isStorage) txReceipt.ContractAddress = context.DecodeAddress();
            if(isStorage) txReceipt.GasUsed = (long) context.DecodeUBigInt();
            txReceipt.GasUsedTotal = (long) context.DecodeUBigInt();
            txReceipt.Bloom = context.DecodeBloom();

            int lastCheck = context.ReadSequenceLength() + context.Position;
            List<LogEntry> logEntries = new List<LogEntry>();

            while (context.Position < lastCheck)
            {
                logEntries.Add(Rlp.Decode<LogEntry>(context, RlpBehaviors.AllowExtraData));
            }

            bool allowExtraData = (rlpBehaviors & RlpBehaviors.AllowExtraData) != 0;
            if (!allowExtraData)
            {
                context.Check(lastCheck);
            }
            
            // since error was added later we can only rely on it in cases where we read receipt only and no data follows
            if (isStorage && !allowExtraData && context.Position != context.Length)
            {
                txReceipt.Error = context.DecodeString();
            }

            txReceipt.Logs = logEntries.ToArray();
            return txReceipt;
        }

        public Rlp Encode(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpBehaviors.HasFlag(RlpBehaviors.Storage))
            {
                return Rlp.Encode(
                    rlpBehaviors.HasFlag(RlpBehaviors.Eip658Receipts) ? Rlp.Encode(item.StatusCode) : Rlp.Encode(item.PostTransactionState),
                    Rlp.Encode(item.BlockHash),
                    Rlp.Encode(item.BlockNumber),
                    Rlp.Encode(item.Index),
                    Rlp.Encode(item.Sender),
                    Rlp.Encode(item.Recipient),
                    Rlp.Encode(item.ContractAddress),
                    Rlp.Encode(item.GasUsed),
                    Rlp.Encode(item.GasUsedTotal),
                    Rlp.Encode(item.Bloom),
                    Rlp.Encode(item.Logs),
                    Rlp.Encode(item.Error));
            }

            return Rlp.Encode(
                rlpBehaviors.HasFlag(RlpBehaviors.Eip658Receipts) ? Rlp.Encode(item.StatusCode) : Rlp.Encode(item.PostTransactionState),
                Rlp.Encode(item.GasUsedTotal),
                Rlp.Encode(item.Bloom),
                Rlp.Encode(item.Logs));
        }

        public void Encode(MemoryStream stream, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}