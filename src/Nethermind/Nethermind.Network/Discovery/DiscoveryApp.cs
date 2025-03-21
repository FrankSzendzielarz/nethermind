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
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;
using Timer = System.Timers.Timer;

namespace Nethermind.Network.Discovery
{
    public class DiscoveryApp : IDiscoveryApp
    {
        private readonly IDiscoveryConfig _discoveryConfig;
        private readonly ITimestamp _timestamp;
        private readonly INodesLocator _nodesLocator;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly INodeTable _nodeTable;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly INetworkStorage _discoveryStorage;
        private readonly IPerfService _perfService;

        private Timer _discoveryTimer;
        private Timer _discoveryPersistenceTimer;

        private IChannel _channel;
        private MultithreadEventLoopGroup _group;
        private NettyDiscoveryHandler _discoveryHandler;
        private Task _storageCommitTask;

        public DiscoveryApp(
            INodesLocator nodesLocator,
            IDiscoveryManager discoveryManager,
            INodeTable nodeTable,
            IMessageSerializationService messageSerializationService,
            ICryptoRandom cryptoRandom,
            INetworkStorage discoveryStorage,
            IDiscoveryConfig discoveryConfig,
            ITimestamp timestamp,
            ILogManager logManager,
            IPerfService perfService)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
            _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
            _nodesLocator = nodesLocator ?? throw new ArgumentNullException(nameof(nodesLocator));
            _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
            _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
            _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
            _discoveryStorage.StartBatch();
        }

        public event EventHandler<NodeEventArgs> NodeDiscovered;
        
        public void Initialize(PublicKey masterPublicKey)
        {
            _discoveryManager.NodeDiscovered += OnNewNodeDiscovered;
            _nodeTable.Initialize(masterPublicKey);
            _nodesLocator.Initialize(_nodeTable.MasterNode);
        }

        public void Start()
        {
            try
            {
                InitializeUdpChannel();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery app start process", e);
                throw;
            }
        }

        public async Task StopAsync()
        {
            var key = _perfService.StartPerfCalc();
            _appShutdownSource.Cancel();
            StopDiscoveryTimer();
            //StopRefreshTimer();
            StopDiscoveryPersistenceTimer();

            if (_storageCommitTask != null)
            {
                await _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Error during discovery persisntance stop.", x.Exception);
                    }
                });
            }

            await StopUdpChannelAsync();
            if(_logger.IsInfo) _logger.Info("Discovery shutdown complete.. please wait for all components to close");
            _perfService.EndPerfCalc(key, "Close: DiscoveryApp");
        }

        public void AddNodeToDiscovery(Node node)
        {
            _discoveryManager.GetNodeLifecycleManager(node);
        }

        private void InitializeUdpChannel()
        {
            if(_logger.IsInfo) _logger.Info($"Discovery    : udp://{_discoveryConfig.MasterHost}:{_discoveryConfig.MasterPort}");
            _group = new MultithreadEventLoopGroup(1);
            var bootstrap = new Bootstrap();
            bootstrap
                .Group(_group);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                bootstrap
                    .ChannelFactory(() => new SocketDatagramChannel(AddressFamily.InterNetwork))
                    .Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));
            }
            else
            {
                bootstrap
                    .Channel<SocketDatagramChannel>()
                    .Handler(new ActionChannelInitializer<IDatagramChannel>(InitializeChannel));
            }

            _bindingTask = bootstrap.BindAsync(IPAddress.Parse(_discoveryConfig.MasterHost), _discoveryConfig.MasterPort)
                .ContinueWith(t => _channel = t.Result);
        }

        private Task _bindingTask;

        private void InitializeChannel(IDatagramChannel channel)
        {
            _discoveryHandler = new NettyDiscoveryHandler(_discoveryManager, channel, _messageSerializationService, _timestamp, _logManager);
            _discoveryManager.MessageSender = _discoveryHandler;
            _discoveryHandler.OnChannelActivated += OnChannelActivated;
            channel.Pipeline
                .AddLast(new LoggingHandler(DotNetty.Handlers.Logging.LogLevel.INFO))
                .AddLast(_discoveryHandler);
        }

        private CancellationTokenSource _appShutdownSource = new CancellationTokenSource();
        
        private void OnChannelActivated(object sender, EventArgs e)
        {
            //Make sure this is non blocking code, otherwise netty will not process messages
            Task.Run(() => OnChannelActivated(_appShutdownSource.Token)).ContinueWith
            (
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Info("Cannot activate channel.");
                        throw t.Exception;
                    }
                    
                    if (t.IsCompleted && !_appShutdownSource.IsCancellationRequested)
                    {
                        _logger.Debug("Discovery App initialized.");
                    }
                }
            );
        }

        private async Task OnChannelActivated(CancellationToken cancellationToken)
        {
            try
            {
                //Step 1 - read nodes and stats from db
                AddPersistedNodes(cancellationToken);

                //Step 2 - initialize bootnodes
                if(_logger.IsDebug) _logger.Debug("Initializing bootnodes.");
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    
                    if (await InitializeBootnodes(cancellationToken))
                    {
                        break;
                    }

                    //Check if we were able to communicate with any trusted nodes or persisted nodes
                    //if so no need to replay bootstrapping, we can start discovery process
                    if (_discoveryManager.GetOrAddNodeLifecycleManagers(x => x.State == NodeLifecycleState.Active).Any())
                    {
                        break;
                    }
                    
                    _logger.Warn("Could not communicate with any nodes (bootnodes, trusted nodes, persisted nodes).");
                    await Task.Delay(1000, cancellationToken); 
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                
                InitializeDiscoveryPersistenceTimer();
                InitializeDiscoveryTimer();

                await RunDiscoveryAsync(cancellationToken).ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                        {
                            _logger.Error("Discovery error", t.Exception);            
                        }
                    });
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery initialization", e);
            }
        }

        private void AddPersistedNodes(CancellationToken cancellationToken)
        {
            if (!_discoveryConfig.IsDiscoveryNodesPersistenceOn)
            {
                return;
            }

            var nodes = _discoveryStorage.GetPersistedNodes();
            foreach (var networkNode in nodes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                
                Node node;
                try
                {
                    node = new Node(networkNode.NodeId, networkNode.Host, networkNode.Port);
                }
                catch (Exception)
                {
                    if(_logger.IsDebug) _logger.Error($"ERROR/DEBUG peer could not be loaded for {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
                    continue;
                }
                
                var manager = _discoveryManager.GetNodeLifecycleManager(node, true);
                if (manager == null)
                {
                    if (_logger.IsDebug)
                    {
                        _logger.Debug($"Skiping persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}, manager couldnt be created");
                    }
                    
                    continue;;
                }
                manager.NodeStats.CurrentPersistedNodeReputation = networkNode.Reputation;
                if (_logger.IsTrace) _logger.Trace($"Adding persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
            }

            if (_logger.IsInfo) _logger.Info($"Added persisted discovery nodes: {nodes.Length}");
        }

        private void InitializeDiscoveryTimer()
        {
            if(_logger.IsDebug) _logger.Debug("Starting discovery timer");
            _discoveryTimer = new Timer(1000) {AutoReset = false};
            _discoveryTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    _discoveryTimer.Enabled = false;
                    RunDiscoveryProcess();
                    var nodesCountAfterDiscovery = _nodeTable.Buckets.Sum(x => x.Items.Count);
                    _discoveryTimer.Interval = nodesCountAfterDiscovery < 100 ? 10 : nodesCountAfterDiscovery < 1000 ? 100 : _discoveryConfig.DiscoveryInterval;
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Discovery timer failed", exception);
                }
                finally
                {
                    _discoveryTimer.Enabled = true;
                }
            };
            _discoveryTimer.Start();
        }
        
        private void StopDiscoveryTimer()
        {
            try
            {
                if(_logger.IsDebug) _logger.Debug("Stopping discovery timer");
                _discoveryTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery timer stop", e);
            }
        }

        private void InitializeDiscoveryPersistenceTimer()
        {
            if(_logger.IsDebug) _logger.Debug("Starting discovery persistence timer");
            _discoveryPersistenceTimer = new Timer(_discoveryConfig.DiscoveryPersistenceInterval) {AutoReset = false};
            _discoveryPersistenceTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    _discoveryPersistenceTimer.Enabled = false;
                    RunDiscoveryCommit();
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Discovery persistence timer failed", exception);
                }
                finally
                {
                    _discoveryPersistenceTimer.Enabled = true;
                }
            };
            _discoveryPersistenceTimer.Start();
        }

        private void StopDiscoveryPersistenceTimer()
        {
            try
            {
                if(_logger.IsDebug) _logger.Debug("Stopping discovery persistence timer");
                _discoveryPersistenceTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during discovery persistence timer stop", e);
            }
        }

        private async Task StopUdpChannelAsync()
        {
            try
            {
                if (_discoveryHandler != null)
                {
                    _discoveryHandler.OnChannelActivated -= OnChannelActivated;
                }
                
                if (_bindingTask != null)
                {
                    await _bindingTask; // if we are still starting
                }

                _logger.Info("Stopping discovery udp channel");
                if (_channel == null)
                {
                    return;
                }
                var closeTask = _channel.CloseAsync();
                if (await Task.WhenAny(closeTask, Task.Delay(_discoveryConfig.UdpChannelCloseTimeout)) != closeTask)
                {
                    _logger.Error($"Could not close udp connection in {_discoveryConfig.UdpChannelCloseTimeout} miliseconds");
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error during udp channel stop process", e);
            }
        }

        private async Task<bool> InitializeBootnodes(CancellationToken cancellationToken)
        {
            var bootnodes = NetworkNode.ParseNodes(_discoveryConfig.Bootnodes, _logger);
            if (!bootnodes.Any())
            {
                if (_logger.IsWarn) _logger.Warn("No bootnodes specified in configuration");
                return true;
            }
            
            var managers = new List<INodeLifecycleManager>();
            for (var i = 0; i < bootnodes.Length; i++)
            {
                var bootnode = bootnodes[i];
                var node = bootnode.NodeId == null
                    ? new Node(bootnode.Host, bootnode.Port)
                    : new Node(bootnode.NodeId, bootnode.Host, bootnode.Port, true);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                if (manager != null)
                {
                    managers.Add(manager);
                }
                else
                {
                    _logger.Warn($"Bootnode config contains self: {bootnode.NodeId}");
                }
            }

            //Wait for pong message to come back from Boot nodes
            var maxWaitTime = _discoveryConfig.BootnodePongTimeout;
            var itemTime = maxWaitTime / 100;
            for (var i = 0; i < 100; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                
                if (managers.Any(x => x.State == NodeLifecycleState.Active))
                {
                    break;
                }

                if (_discoveryManager.GetOrAddNodeLifecycleManagers(x => x.State == NodeLifecycleState.Active).Any())
                {
                    if(_logger.IsTrace) _logger.Trace("Was not able to connect to any of the bootnodes, but successfully connected to at least one persisted node.");
                    break;
                }

                if (_logger.IsTrace) _logger.Trace($"Waiting {itemTime} ms for bootnodes to respond");
                
                try
                {
                    await Task.Delay(itemTime, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            var reachedNodeCounter = 0;
            for (var i = 0; i < managers.Count; i++)
            {
                var manager = managers[i];
                if (manager.State != NodeLifecycleState.Active)
                {
                    if (_logger.IsTrace) _logger.Trace($"Could not reach bootnode: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Reached bootnode: {manager.ManagedNode.Host}:{manager.ManagedNode.Port}");
                    reachedNodeCounter++;
                }
            }

            if (_logger.IsInfo) _logger.Info($"Connected to {reachedNodeCounter} bootnodes, {_discoveryManager.GetOrAddNodeLifecycleManagers(x => x.State == NodeLifecycleState.Active).Count} trusted/persisted nodes");
            return reachedNodeCounter > 0;
        }

        private void RunDiscoveryProcess()
        {
            var task = Task.Run(async () =>
            {
                await RunDiscoveryAsync(_appShutdownSource.Token);
                await RunRefreshAsync(_appShutdownSource.Token);
            }).ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                {
                    _logger.Error($"Error during discovery process: {x.Exception}");
                }
            });
            task.Wait();
        }

        private async Task RunDiscoveryAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsTrace) _logger.Trace("Running discovery process.");
            await _nodesLocator.LocateNodesAsync(cancellationToken);
        }

        private async Task RunRefreshAsync(CancellationToken cancellationToken)
        {
            if (_logger.IsTrace) _logger.Trace("Running refresh process.");            
            var randomId = _cryptoRandom.GenerateRandomBytes(64);
            await _nodesLocator.LocateNodesAsync(randomId, cancellationToken);
        }

        [Todo(Improve.Allocations, "Remove ToArray here - address as a part of the network DB rewrite")]
        private void RunDiscoveryCommit()
        {
            try
            {
                var managers = _discoveryManager.GetOrAddNodeLifecycleManagers();
                //we need to update all notes to update reputation
                _discoveryStorage.UpdateNodes(managers.Select(x => new NetworkNode(x.ManagedNode.Id, x.ManagedNode.Host, x.ManagedNode.Port, x.NodeStats.NewPersistedNodeReputation)).ToArray());

                if (!_discoveryStorage.AnyPendingChange())
                {
                    if (_logger.IsTrace) _logger.Trace("No changes in discovery storage, skipping commit.");
                    return;
                }

                _storageCommitTask = Task.Run(() =>
                {
                    _discoveryStorage.Commit();
                    _discoveryStorage.StartBatch();
                });

                var task = _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsError)
                    {
                        _logger.Error($"Error during discovery commit: {x.Exception}");
                    }
                });
                task.Wait();
                _storageCommitTask = null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during discovery commit: {ex}");
            }
        }
        
        private void OnNewNodeDiscovered(object sender, NodeEventArgs e)
        {
            e.Node.AddedToDiscovery = true;
            NodeDiscovered?.Invoke(this, e);
        }
    }
}