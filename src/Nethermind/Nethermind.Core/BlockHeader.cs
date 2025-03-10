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
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Hash} ({Number})")]
    public class BlockHeader
    {
        internal BlockHeader()
        {
        }

        public BlockHeader(Keccak parentHash, Keccak ommersHash, Address beneficiary, UInt256 difficulty, long number, long gasLimit, UInt256 timestamp, byte[] extraData)
        {
            ParentHash = parentHash;
            OmmersHash = ommersHash;
            Beneficiary = beneficiary;
            Difficulty = difficulty;
            Number = number;
            GasLimit = gasLimit;
            Timestamp = timestamp;
            ExtraData = extraData;
        }

        public bool IsGenesis => Number == 0;
        public Keccak ParentHash { get; internal set; }
        public Keccak OmmersHash { get; set; }
        public Address Author { get; set; }
        public Address Beneficiary { get; set; }
        public Address GasBeneficiary => Author ?? Beneficiary;
        public Keccak StateRoot { get; set; }
        public Keccak TxRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Bloom Bloom { get; set; }
        public UInt256 Difficulty { get; set; }
        public long Number { get; internal set; }
        public long GasUsed { get; set; }
        public long GasLimit { get; internal set; }
        public UInt256 Timestamp { get; set; }
        public DateTime TimestampDate => DateTimeOffset.FromUnixTimeSeconds((long) Timestamp).DateTime;
        public byte[] ExtraData { get; set; }
        public Keccak MixHash { get; set; }
        public ulong Nonce { get; set; }
        public Keccak Hash { get; set; }
        public UInt256? TotalDifficulty { get; set; }
        public SealEngineType SealEngineType { get; set; } = SealEngineType.Ethash;

        private static ThreadLocal<byte[]> _rlpBuffer = new ThreadLocal<byte[]>(() => new byte[1024]);

        public static Keccak CalculateHash(BlockHeader header)
        {
            using (MemoryStream stream = Rlp.BorrowStream())
            {
                Rlp.Encode(stream, header);
                byte[] buffer = _rlpBuffer.Value;
                stream.Seek(0, SeekOrigin.Begin);
                stream.Read(buffer, 0, (int) stream.Length);
                Keccak newOne = Keccak.Compute(buffer.AsSpan().Slice(0, (int) stream.Length));
                return newOne;
            }
        }

        public static Keccak CalculateHash(Block block)
        {
            return CalculateHash(block.Header);
        }

        public string ToString(string indent)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"{indent}Hash: {Hash}");
            builder.AppendLine($"{indent}Number: {Number}");
            builder.AppendLine($"{indent}Parent: {ParentHash}");
            builder.AppendLine($"{indent}Beneficiary: {Beneficiary}");
            builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
            builder.AppendLine($"{indent}Gas Used: {GasUsed}");
            builder.AppendLine($"{indent}Timestamp: {Timestamp}");
            builder.AppendLine($"{indent}Extra Data: {(ExtraData ?? new byte[0]).ToHexString()}");
            builder.AppendLine($"{indent}Difficulty: {Difficulty}");
            builder.AppendLine($"{indent}Mix Hash: {MixHash}");
            builder.AppendLine($"{indent}Nonce: {Nonce}");
            builder.AppendLine($"{indent}Ommers Hash: {OmmersHash}");
            builder.AppendLine($"{indent}Tx Root: {TxRoot}");
            builder.AppendLine($"{indent}Receipts Root: {ReceiptsRoot}");
            builder.AppendLine($"{indent}State Root: {StateRoot}");
            
            return builder.ToString();
        }

        public override string ToString()
        {
            return ToString(string.Empty);
        }

        public string ToString(Format format)
        {
            switch (format)
            {
                case Format.Full:
                    return ToString(string.Empty);
                default:
                    if (Hash == null)
                    {
                        return $"{Number} null";
                    }
                    else
                    {
                        return $"{Number} ({Hash.Bytes.ToHexString().Substring(58, 6)})";
                    }
            }
        }

        [Todo(Improve.Refactor, "Use IFormattable here")]
        public enum Format
        {
            Full,
            Short
        }
    }
}