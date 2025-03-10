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


using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.WebSockets;

namespace Nethermind.JsonRpc.WebSockets
{
    public class JsonRpcWebSocketsModule : IWebSocketsModule
    {
        private readonly JsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonSerializer _jsonSerializer;
        public string Name { get; } = "json-rpc";

        public JsonRpcWebSocketsModule(JsonRpcProcessor jsonRpcProcessor, IJsonSerializer jsonSerializer)
        {
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonSerializer = jsonSerializer;
        }

        public bool TryInit(HttpRequest request)
        {
            return true;
        }

        public void AddClient(IWebSocketsClient client)
        {
        }

        public async Task ExecuteAsync(IWebSocketsClient client, byte[] data)
        {
            var result = await _jsonRpcProcessor.ProcessAsync(Encoding.UTF8.GetString(data));
            if (result.IsCollection)
            {
                await client.SendAsync(_jsonSerializer.Serialize(result.Responses));
                return;
            }

            await client.SendAsync(_jsonSerializer.Serialize(result.Responses.SingleOrDefault()));
        }

        public void Cleanup(IWebSocketsClient client)
        {
        }
    }
}