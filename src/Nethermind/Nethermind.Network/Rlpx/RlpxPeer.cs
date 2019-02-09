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
using System.Net;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx
{
    public class RlpxPeer : IRlpxPeer
    {
        private IChannel _bootstrapChannel;
        private IEventLoopGroup _bossGroup;
        private IEventLoopGroup _workerGroup;

        private bool _isInitialized;
        public PublicKey LocalNodeId { get; private set; }
        private readonly int _localPort;
        private readonly IEncryptionHandshakeService _encryptionHandshakeService;
        private readonly IMessageSerializationService _serializationService;
        private readonly ISynchronizationManager _synchronizationManager;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IPerfService _perfService;
        private readonly IBlockTree _blockTree;
        private readonly ISessionMonitor _sessionMonitor;
        private readonly ITransactionPool _transactionPool;
        private readonly ITimestamp _timestamp;

        public RlpxPeer(
            PublicKey localNodeId,
            int localPort,
            ISynchronizationManager synchronizationManager,
            IMessageSerializationService messageSerializationService,
            IEncryptionHandshakeService encryptionHandshakeService,
            ILogManager logManager,
            IPerfService perfService,
            IBlockTree blockTree,
            ISessionMonitor sessionMonitor,
            ITransactionPool transactionPool)
        {
            _encryptionHandshakeService = encryptionHandshakeService ??
                                          throw new ArgumentNullException(nameof(encryptionHandshakeService));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _sessionMonitor = sessionMonitor ?? throw new ArgumentNullException(nameof(sessionMonitor));
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _timestamp = new Timestamp();
            _logger = logManager.GetClassLogger();
            _serializationService = messageSerializationService ??
                                    throw new ArgumentNullException(nameof(messageSerializationService));
            _synchronizationManager =
                synchronizationManager ?? throw new ArgumentNullException(nameof(synchronizationManager));

            LocalNodeId = localNodeId ?? throw new ArgumentNullException(nameof(localNodeId));
            _localPort = localPort;
        }

        public async Task Init()
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException($"{nameof(PeerManager)} already initialized.");
            }

            _isInitialized = true;

            try
            {
                _bossGroup = new MultithreadEventLoopGroup(1);
                _workerGroup = new MultithreadEventLoopGroup();

                ServerBootstrap bootstrap = new ServerBootstrap();
                bootstrap
                    .Group(_bossGroup, _workerGroup)
                    .Channel<TcpServerSocketChannel>()
                    .ChildOption(ChannelOption.SoBacklog, 100)
                    .Handler(new LoggingHandler("BOSS", DotNetty.Handlers.Logging.LogLevel.TRACE))
                    .ChildHandler(new ActionChannelInitializer<ISocketChannel>(ch =>
                    {
                        InitializeChannel(ch, ConnectionDirection.In, null,
                            ((IPEndPoint) ch.RemoteAddress).Address.ToString(), ((IPEndPoint) ch.RemoteAddress).Port);
                    }));

                _bootstrapChannel = await bootstrap.BindAsync(_localPort).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error($"{nameof(Init)} failed", t.Exception);
                        return null;
                    }

                    return t.Result;
                });

                if (_bootstrapChannel == null)
                {
                    throw new NetworkingException($"Failed to initialize {nameof(_bootstrapChannel)}", NetworkExceptionType.Other);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"{nameof(Init)} failed.", ex);
                // TODO: check what happens on nulls
                await Task.WhenAll(_bossGroup?.ShutdownGracefullyAsync(), _workerGroup?.ShutdownGracefullyAsync());
                throw;
            }
        }

        public async Task ConnectAsync(Node node)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Connecting to {node.Id}@{node.Port}:{node.Host}");

            Bootstrap clientBootstrap = new Bootstrap();
            clientBootstrap.Group(_workerGroup);
            clientBootstrap.Channel<TcpSocketChannel>();

            clientBootstrap.Option(ChannelOption.TcpNodelay, true);
            clientBootstrap.Option(ChannelOption.MessageSizeEstimator, DefaultMessageSizeEstimator.Default);
            clientBootstrap.Option(ChannelOption.ConnectTimeout, Timeouts.InitialConnection);
            clientBootstrap.RemoteAddress(node.Host, node.Port);
            
            clientBootstrap.Handler(new ActionChannelInitializer<ISocketChannel>(ch =>
            {
                InitializeChannel(ch, ConnectionDirection.Out, node.Id, node.Host, node.Port);
            }));

            var connectTask = clientBootstrap.ConnectAsync(new IPEndPoint(IPAddress.Parse(node.Host), node.Port));
            var firstTask = await Task.WhenAny(connectTask, Task.Delay(Timeouts.InitialConnection.Add(TimeSpan.FromSeconds(10))));
            if (firstTask != connectTask)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Connection timed out: {node.Id}@{node.Port}:{node.Host}");
                throw new NetworkingException($"Failed to connect to {node.Id}@{node.Port}:{node.Host} (timeout)", NetworkExceptionType.Timeout);
            }

            if (connectTask.IsFaulted)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Error when connecting to {node.Id}@{node.Port}:{node.Host}, error: {connectTask.Exception}");
                }

                throw new NetworkingException($"Failed to connect to {node.Id}@{node.Port}:{node.Host}", NetworkExceptionType.TargetUnreachable,connectTask.Exception);
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Connected to {node.Id}@{node.Port}:{node.Host}");
        }

        public event EventHandler<SessionEventArgs> SessionCreated;
        
        private void InitializeChannel(IChannel channel, ConnectionDirection connectionDirection, PublicKey remoteId = null, string remoteHost = null, int? remotePort = null, INodeStats nodeStats = null)
        {
            if (connectionDirection == ConnectionDirection.In)
            {
                Metrics.IncomingConnections++;
            }
            else
            {
                Metrics.OutgoingConnections++;    
            }

            P2PSession session = new P2PSession(
                LocalNodeId,
                remoteId,
                _localPort,
                connectionDirection,
                _serializationService,
                _synchronizationManager,
                _logManager, channel, _perfService, _blockTree, _transactionPool, _timestamp);

            if (connectionDirection == ConnectionDirection.Out)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Initializing {connectionDirection.ToString().ToUpper()} channel{(connectionDirection == ConnectionDirection.Out ? $": {remoteId}@{remoteHost}:{remotePort}" : string.Empty)}");
                }

                // this is the first moment we get confirmed publicKey of remote node in case of outgoing connections
                session.RemoteNodeId = remoteId;
                session.RemoteHost = remoteHost;
                session.RemotePort = remotePort;
            }
            
            _sessionMonitor.AddSession(session);
            session.SessionDisconnected += SessionOnPeerDisconnected;
            SessionCreated?.Invoke(this, new SessionEventArgs(session));

            HandshakeRole role = connectionDirection == ConnectionDirection.In ? HandshakeRole.Recipient : HandshakeRole.Initiator;
            var handshakeHandler = new NettyHandshakeHandler(_encryptionHandshakeService, session, role, remoteId, _logManager);
            
            IChannelPipeline pipeline = channel.Pipeline;
            pipeline.AddLast(new LoggingHandler(connectionDirection.ToString().ToUpper(), DotNetty.Handlers.Logging.LogLevel.TRACE));
            pipeline.AddLast("enc-handshake-dec", new LengthFieldBasedFrameDecoder(ByteOrder.BigEndian, ushort.MaxValue, 0, 2, 0, 0, true));
            pipeline.AddLast("enc-handshake-handler", handshakeHandler);

            channel.CloseCompletion.ContinueWith(async x =>
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Channel disconnected: {session.RemoteNodeId}");
                }
                
                await session.DisconnectAsync(DisconnectReason.ClientQuitting, DisconnectType.Remote);
            });
        }

        private void SessionOnPeerDisconnected(object sender, DisconnectEventArgs e)
        {
            IP2PSession session = (P2PSession) sender;
            session.SessionDisconnected -= SessionOnPeerDisconnected;
            session.Dispose();
        }

        public async Task Shutdown()
        {
            var key = _perfService.StartPerfCalc();
//            InternalLoggerFactory.DefaultFactory.AddProvider(new ConsoleLoggerProvider((s, level) => true, false));

            await _bootstrapChannel.CloseAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(Shutdown)} failed", t.Exception);
                }
            });

            _logger.Debug("Closed _bootstrapChannel");

            var nettyCloseTimeout = TimeSpan.FromMilliseconds(100);
            var closingTask = Task.WhenAll(_bossGroup.ShutdownGracefullyAsync(nettyCloseTimeout, nettyCloseTimeout),
                _workerGroup.ShutdownGracefullyAsync(nettyCloseTimeout, nettyCloseTimeout));
                
            //we need to add additional timeout on our side as netty is not executing internal timeout properly, often it just hangs forever on closing
            if (await Task.WhenAny(closingTask, Task.Delay(Timeouts.TcpClose)) != closingTask)
            {
                _logger.Warn($"Could not close rlpx connection in {Timeouts.TcpClose.TotalSeconds} seconds");
            }

            if(_logger.IsInfo) _logger.Info("Local peer shutdown complete.. please wait for all components to close");
            _perfService.EndPerfCalc(key, "Close: Rlpx");
        }
    }
}