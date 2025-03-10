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

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Cli.Modules
{
    [CliModule("eth")]
    public class EthCliModule : CliModuleBase
    {
        private string SendEth(Address from, Address address, UInt256 amountInWei)
        {
            long blockNumber = NodeManager.Post<long>("eth_blockNumber").Result;

            TransactionForRpc tx = new TransactionForRpc();
            tx.Value = amountInWei;
            tx.Gas = 21000;
            tx.GasPrice = (UInt256) Engine.JintEngine.GetValue("gasPrice").AsNumber();
            tx.To = address;
            tx.Nonce = (ulong) NodeManager.Post<long>("eth_getTransactionCount", address, blockNumber).Result;
            tx.From = from;

            Keccak keccak = NodeManager.Post<Keccak>("eth_sendTransaction", tx).Result;
            return keccak.Bytes.ToHexString();
        }

        [CliFunction("eth", "getBlockByHash")]
        public object GetBlockByHash(string hash, bool returnFullTransactionObjects)
        {
            return NodeManager.Post<object>("eth_getBlockByHash", CliParseHash(hash), returnFullTransactionObjects).Result;
        }
        
        [CliFunction("eth", "getBlockByNumber")]
        public object GetBlockByNumber(string blockParameter, bool returnFullTransactionObjects)
        {
            return NodeManager.Post<object>("eth_getBlockByNumber", blockParameter, returnFullTransactionObjects).Result;
        }

        [CliFunction("eth", "sendEth")]
        public string SendEth(string from, string to, decimal amountInEth)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256) (amountInEth * (decimal) 1.Ether()));
        }

        [CliFunction("eth", "sendWei")]
        public string SendWei(string from, string to, BigInteger amountInWei)
        {
            return SendEth(CliParseAddress(from), CliParseAddress(to), (UInt256) amountInWei);
        }

        [CliProperty("eth", "blockNumber")]
        public long BlockNumber()
        {
            return NodeManager.Post<long>("eth_blockNumber").Result;
        }

        [CliFunction("eth", "getCode")]
        public string GetCode(string address, string blockParameter)
        {
            return NodeManager.Post<string>("eth_getCode", address, blockParameter).Result;
        }

        [CliFunction("eth", "getBlockTransactionCountByNumber")]
        public string GetBlockTransactionCountByNumber(string blockParameter)
        {
            return NodeManager.Post<string>("eth_getBlockTransactionCountByNumber", blockParameter).Result;
        }

        [CliFunction("eth", "getBlockTransactionCountByHash")]
        public string GetBlockTransactionCountByHash(string hash)
        {
            return NodeManager.Post<string>("eth_getBlockTransactionCountByHash", hash).Result;
        }

        [CliFunction("eth", "getUncleCountByBlockNumber")]
        public string GetUncleCountByBlockNumber(string blockParameter)
        {
            return NodeManager.Post<string>("eth_getUncleCountByBlockNumber", blockParameter).Result;
        }

        [CliFunction("eth", "getTransactionByBlockNumberAndIndex")]
        public string GetTransactionByBlockNumberAndIndex(string blockParameter, string index)
        {
            return NodeManager.Post<string>("eth_getTransactionByBlockNumberAndIndex", blockParameter, index).Result;
        }

        [CliFunction("eth", "getTransactionReceipt")]
        public string GetTransactionReceipt(string txHash)
        {
            return NodeManager.Post<string>("eth_getTransactionReceipt", txHash).Result;
        }

        [CliFunction("eth", "getBalance")]
        public string GetBalance(string address, string blockParameter)
        {
            return NodeManager.Post<string>("eth_getBalance", CliParseAddress(address), blockParameter).Result;
        }

        [CliProperty("eth", "protocolVersion")]
        public int ProtocolVersion()
        {
            return NodeManager.Post<int>("eth_protocolVersion").Result;
        }

        public EthCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}