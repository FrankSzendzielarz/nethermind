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
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Client
{
    public class BasicJsonRpcClient : IJsonRpcClient
    {
        private readonly HttpClient _client;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        public BasicJsonRpcClient(Uri uri, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonSerializer = jsonSerializer;

            _client = new HttpClient {BaseAddress = uri};
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<string> Post(string method, params object[] parameters)
        {
            string request = GetJsonRequest(method, parameters);
            HttpResponseMessage response = await _client.PostAsync("", new StringContent(request, Encoding.UTF8, "application/json"));
            string content = await response.Content.ReadAsStringAsync();
            return content;
        }

        public async Task<T> Post<T>(string method, params object[] parameters)
        {
            string responseString = string.Empty;
            try
            {
                string request = GetJsonRequest(method, parameters);
                HttpResponseMessage response = await _client.PostAsync("", new StringContent(request, Encoding.UTF8, "application/json"));
                responseString = await response.Content.ReadAsStringAsync();
                if(_logger.IsTrace) _logger.Trace(responseString);

                JsonRpcResponse<T> jsonResponse = _jsonSerializer.Deserialize<JsonRpcResponse<T>>(responseString);
                if (jsonResponse.Error != null)
                {
                    if(_logger.IsError) _logger.Error(jsonResponse.Error.Message);
                }
                
                return jsonResponse.Result;
            }
            catch (NotImplementedException)
            {
                throw;
            }
            catch (NotSupportedException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new DataException($"Cannot deserialize {responseString}");
            }
        }

        private string GetJsonRequest(string method, IEnumerable<object> parameters)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method,
                Params = parameters ?? Enumerable.Empty<object>(),
                id = 67
            };

            return _jsonSerializer.Serialize(request);
        }
    }
}