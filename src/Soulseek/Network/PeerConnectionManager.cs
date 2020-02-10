﻿// <copyright file="PeerConnectionManager.cs" company="JP Dillingham">
//     Copyright (c) JP Dillingham. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
//     as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
//     of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License along with this program. If not, see https://www.gnu.org/licenses/.
// </copyright>

namespace Soulseek.Network
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Soulseek.Diagnostics;
    using Soulseek.Exceptions;
    using Soulseek.Messaging.Messages;
    using Soulseek.Network.Tcp;

    /// <summary>
    ///     Manages peer <see cref="IConnection"/> instances for the application.
    /// </summary>
    internal sealed class PeerConnectionManager : IPeerConnectionManager
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="PeerConnectionManager"/> class.
        /// </summary>
        /// <param name="soulseekClient">The ISoulseekClient instance to use.</param>
        /// <param name="connectionFactory">The IConnectionFactory instance to use.</param>
        /// <param name="diagnosticFactory">The IDiagnosticFactory instance to use.</param>
        internal PeerConnectionManager(
            SoulseekClient soulseekClient,
            IConnectionFactory connectionFactory = null,
            IDiagnosticFactory diagnosticFactory = null)
        {
            SoulseekClient = soulseekClient ?? throw new ArgumentNullException(nameof(soulseekClient));

            ConnectionFactory = connectionFactory ?? new ConnectionFactory();

            Diagnostic = diagnosticFactory ??
                new DiagnosticFactory(this, SoulseekClient?.Options?.MinimumDiagnosticLevel ?? new SoulseekClientOptions().MinimumDiagnosticLevel, (e) => DiagnosticGenerated?.Invoke(this, e));
        }

        /// <summary>
        ///     Occurs when an internal diagnostic message is generated.
        /// </summary>
        public event EventHandler<DiagnosticEventArgs> DiagnosticGenerated;

        /// <summary>
        ///     Gets current list of peer message connections.
        /// </summary>
        public IReadOnlyCollection<(string Username, IPEndPoint IPEndPoint)> MessageConnections => MessageConnectionDictionary.Values
            .Select(async c => await c.Value.ConfigureAwait(false))
            .Select(c => (c.Result.Username, c.Result.IPEndPoint)).ToList().AsReadOnly();

        /// <summary>
        ///     Gets a dictionary containing the pending connection solicitations.
        /// </summary>
        public IReadOnlyDictionary<int, string> PendingSolicitations => new ReadOnlyDictionary<int, string>(PendingSolicitationDictionary);

        private IConnectionFactory ConnectionFactory { get; }
        private IDiagnosticFactory Diagnostic { get; }
        private bool Disposed { get; set; }

        private ConcurrentDictionary<string, Lazy<Task<IMessageConnection>>> MessageConnectionDictionary { get; set; } =
            new ConcurrentDictionary<string, Lazy<Task<IMessageConnection>>>();

        private ConcurrentDictionary<int, string> PendingSolicitationDictionary { get; set; } = new ConcurrentDictionary<int, string>();
        private SoulseekClient SoulseekClient { get; }

        /// <summary>
        ///     Adds a new message connection from an incoming connection.
        /// </summary>
        /// <remarks>
        ///     This method will be invoked from <see cref="ListenerHandler"/> upon receipt of an incoming unsolicited message only.  Because this connection is fully established by the time it is passed to this method, it must supercede any cached connection, as it will be the most recently established connection as tracked by the remote user.
        /// </remarks>
        /// <param name="username">The username from which the connection originated.</param>
        /// <param name="incomingConnection">The the accepted connection.</param>
        /// <returns>The operation context.</returns>
        public async Task AddMessageConnectionAsync(string username, IConnection incomingConnection)
        {
            await MessageConnectionDictionary.AddOrUpdate(
                username,
                new Lazy<Task<IMessageConnection>>(() => Task.Run(() => GetConnection())),
                (key, cachedConnectionRecord) => new Lazy<Task<IMessageConnection>>(() => Task.Run(() => GetConnection(cachedConnectionRecord)))).Value.ConfigureAwait(false);

            async Task<IMessageConnection> GetConnection(Lazy<Task<IMessageConnection>> cachedConnectionRecord = null)
            {
                Diagnostic.Debug($"Inbound message connection to {username} ({incomingConnection.IPEndPoint}) accepted. (type: {incomingConnection.Type}, id: {incomingConnection.Id}");

                var endpoint = incomingConnection.IPEndPoint;

                var connection = ConnectionFactory.GetMessageConnection(
                    username,
                    endpoint,
                    SoulseekClient.Options.PeerConnectionOptions,
                    incomingConnection.HandoffTcpClient());

                connection.Type = ConnectionTypes.Inbound | ConnectionTypes.Direct;
                connection.MessageRead += SoulseekClient.PeerMessageHandler.HandleMessageRead;
                connection.Disconnected += MessageConnection_Disconnected;

                Diagnostic.Debug($"Inbound message connection to {username} ({connection.IPEndPoint}) handed off. (old: {incomingConnection.Id}, new: {connection.Id})");

                if (cachedConnectionRecord != null)
                {
                    var cachedConnection = await cachedConnectionRecord.Value.ConfigureAwait(false);
                    cachedConnection.Disconnected -= MessageConnection_Disconnected;
                    Diagnostic.Debug($"Superceding cached message connection to {username} ({cachedConnection.IPEndPoint}) (old: {cachedConnection.Id}, new: {connection.Id}");
                }

                Diagnostic.Debug($"Message connection to {username} ({connection.IPEndPoint}) established. (type: {connection.Type}, id: {connection.Id})");

                connection.StartReadingContinuously();
                return connection;
            }
        }

        /// <summary>
        ///     Adds a new transfer connection from an incoming connection.
        /// </summary>
        /// <param name="username">The username from which the connection originated.</param>
        /// <param name="token">The token with which the firewall was pierced.</param>
        /// <param name="incomingConnection">The accepted connection.</param>
        /// <returns>The operation context.</returns>
        public async Task AddTransferConnectionAsync(string username, int token, IConnection incomingConnection)
        {
            Diagnostic.Debug($"Inbound transfer connection to {username} ({incomingConnection.IPEndPoint}) for token {token} accepted. (type: {incomingConnection.Type}, id: {incomingConnection.Id}");

            var connection = ConnectionFactory.GetConnection(
                incomingConnection.IPEndPoint,
                SoulseekClient.Options.TransferConnectionOptions,
                incomingConnection.HandoffTcpClient());

            connection.Type = ConnectionTypes.Inbound | ConnectionTypes.Direct;
            connection.Disconnected += (sender, e) => Diagnostic.Debug($"Transfer connection to {username} ({connection.IPEndPoint}) for token {token} disconnected. (type: {connection.Type}, id: {connection.Id})");

            Diagnostic.Debug($"Inbound transfer connection to {username} ({connection.IPEndPoint}) for token {token} handed off. (old: {incomingConnection.Id}, new: {connection.Id})");

            int remoteToken;

            try
            {
                var remoteTokenBytes = await connection.ReadAsync(4).ConfigureAwait(false);
                remoteToken = BitConverter.ToInt32(remoteTokenBytes, 0);
            }
            catch (Exception)
            {
                connection.Dispose();
                throw;
            }

            Diagnostic.Debug($"Transfer connection to {username} ({connection.IPEndPoint}) for remote token {remoteToken} established. (type: {connection.Type}, id: {connection.Id})");
            SoulseekClient.Waiter.Complete(new WaitKey(Constants.WaitKey.DirectTransfer, username, remoteToken), connection);
        }

        /// <summary>
        ///     Releases the managed and unmanaged resources used by the <see cref="IPeerConnectionManager"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Returns an existing, or gets a new connection using the details in the specified
        ///     <paramref name="connectToPeerResponse"/> and pierces the remote peer's firewall.
        /// </summary>
        /// <remarks>
        ///     This method will be invoked from <see cref="Messaging.Handlers.ServerMessageHandler"/> upon receipt of an unsolicited <see cref="ConnectToPeerResponse"/> of type 'P' only. This connection should only be initiated if there is no existing connection; superceding should be avoided if possible.
        /// </remarks>
        /// <param name="connectToPeerResponse">The response that solicited the connection.</param>
        /// <returns>The operation context, including the new or updated connection.</returns>
        public async Task<IMessageConnection> GetOrAddMessageConnectionAsync(ConnectToPeerResponse connectToPeerResponse)
        {
            return await MessageConnectionDictionary.GetOrAdd(
                connectToPeerResponse.Username,
                key => new Lazy<Task<IMessageConnection>>(() => Task.Run(() => GetConnection()))).Value.ConfigureAwait(false);

            async Task<IMessageConnection> GetConnection()
            {
                Diagnostic.Debug($"Attempting indirect message connection to {connectToPeerResponse.Username} ({connectToPeerResponse.IPEndPoint}) for token {connectToPeerResponse.Token}");

                var connection = ConnectionFactory.GetMessageConnection(
                    connectToPeerResponse.Username,
                    connectToPeerResponse.IPEndPoint,
                    SoulseekClient.Options.PeerConnectionOptions);

                connection.Type = ConnectionTypes.Inbound | ConnectionTypes.Indirect;
                connection.MessageRead += SoulseekClient.PeerMessageHandler.HandleMessageRead;
                connection.MessageReceived += SoulseekClient.PeerMessageHandler.HandleMessageReceived;
                connection.Disconnected += MessageConnection_Disconnected;

                try
                {
                    await connection.ConnectAsync().ConfigureAwait(false);

                    var request = new PierceFirewall(connectToPeerResponse.Token).ToByteArray();
                    await connection.WriteAsync(request).ConfigureAwait(false);
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }

                Diagnostic.Debug($"Message connection to {connectToPeerResponse.Username} ({connectToPeerResponse.IPEndPoint}) established. (type: {connection.Type}, id: {connection.Id})");
                return connection;
            }
        }

        /// <summary>
        ///     Gets a new or existing message connection to the specified <paramref name="username"/>.
        /// </summary>
        /// <remarks>
        ///     If a connection doesn't exist, new direct and indirect connections are attempted simultaneously, and the first to
        ///     connect is returned.
        /// </remarks>
        /// <param name="username">The username of the user to which to connect.</param>
        /// <param name="ipEndPoint">The remote IP endpoint of the connection.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>The operation context, including the new or existing connection.</returns>
        public async Task<IMessageConnection> GetOrAddMessageConnectionAsync(string username, IPEndPoint ipEndPoint, CancellationToken cancellationToken)
        {
            return await MessageConnectionDictionary.GetOrAdd(
                username,
                key => new Lazy<Task<IMessageConnection>>(() => Task.Run(() => GetConnection()))).Value.ConfigureAwait(false);

            async Task<IMessageConnection> GetConnection()
            {
                using (var directCts = new CancellationTokenSource())
                using (var directLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, directCts.Token))
                using (var indirectCts = new CancellationTokenSource())
                using (var indirectLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, indirectCts.Token))
                {
                    Diagnostic.Debug($"Attempting simultaneous direct and indirect message connections to {username} ({ipEndPoint})");

                    var direct = GetMessageConnectionOutboundDirectAsync(username, ipEndPoint, directLinkedCts.Token);
                    var indirect = GetMessageConnectionOutboundIndirectAsync(username, indirectLinkedCts.Token);

                    var tasks = new[] { direct, indirect }.ToList();
                    Task<IMessageConnection> task;

                    do
                    {
                        task = await Task.WhenAny(tasks).ConfigureAwait(false);
                        tasks.Remove(task);
                    }
                    while (task.Status != TaskStatus.RanToCompletion && tasks.Count > 0);

                    if (task.Status != TaskStatus.RanToCompletion)
                    {
                        throw new ConnectionException($"Failed to establish a direct or indirect message connection to {username} ({ipEndPoint})");
                    }

                    var connection = await task.ConfigureAwait(false);
                    connection.Disconnected += MessageConnection_Disconnected;

                    var isDirect = task == direct;

                    Diagnostic.Debug($"{(isDirect ? "Direct" : "Indirect")} message connection to {username} ({ipEndPoint}) established first, attempting to cancel {(isDirect ? "indirect" : "direct")} connection.");
                    (isDirect ? indirectCts : directCts).Cancel();

                    if (isDirect)
                    {
                        var request = new PeerInit(SoulseekClient.Username, Constants.ConnectionType.Peer, SoulseekClient.GetNextToken()).ToByteArray();
                        await connection.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        connection.StartReadingContinuously();
                    }

                    Diagnostic.Debug($"Message connection to {username} ({ipEndPoint}) established. (type: {connection.Type}, id: {connection.Id})");
                    return connection;
                }
            }
        }

        /// <summary>
        ///     Gets a new transfer connection using the details in the specified <paramref name="connectToPeerResponse"/>,
        ///     pierces the remote peer's firewall, and retrieves the remote token.
        /// </summary>
        /// <param name="connectToPeerResponse">The response that solicited the connection.</param>
        /// <returns>The operation context, including the new connection and the associated remote token.</returns>
        public async Task<(IConnection Connection, int RemoteToken)> GetTransferConnectionAsync(ConnectToPeerResponse connectToPeerResponse)
        {
            Diagnostic.Debug($"Attempting indirect transfer connection to {connectToPeerResponse.Username} ({connectToPeerResponse.IPEndPoint}) for token {connectToPeerResponse.Token}");

            var connection = ConnectionFactory.GetConnection(
                connectToPeerResponse.IPEndPoint,
                SoulseekClient.Options.TransferConnectionOptions);

            connection.Type = ConnectionTypes.Inbound | ConnectionTypes.Indirect;
            connection.Disconnected += (sender, e) => Diagnostic.Debug($"Transfer connection to {connectToPeerResponse.Username} ({connectToPeerResponse.IPEndPoint}) for token {connectToPeerResponse.Token} disconnected. (type: {connection.Type}, id: {connection.Id})");

            int remoteToken;

            try
            {
                await connection.ConnectAsync().ConfigureAwait(false);

                var request = new PierceFirewall(connectToPeerResponse.Token);
                await connection.WriteAsync(request.ToByteArray()).ConfigureAwait(false);

                var remoteTokenBytes = await connection.ReadAsync(4).ConfigureAwait(false);
                remoteToken = BitConverter.ToInt32(remoteTokenBytes, 0);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            Diagnostic.Debug($"Transfer connection to {connectToPeerResponse.Username} ({connectToPeerResponse.IPEndPoint}) for token {connectToPeerResponse.Token} established. (type: {connection.Type}, id: {connection.Id})");
            return (connection, remoteToken);
        }

        /// <summary>
        ///     Gets a new transfer connection to the specified <paramref name="username"/> using the specified <paramref name="token"/>.
        /// </summary>
        /// <remarks>A direct connection is attempted first, and, if unsuccessful, an indirect connection is attempted.</remarks>
        /// <param name="username">The username of the user to which to connect.</param>
        /// <param name="ipEndPoint">The remote IP endpoint of the connection.</param>
        /// <param name="token">The token with which to initialize the connection.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>The operation context, including the new connection.</returns>
        public async Task<IConnection> GetTransferConnectionAsync(string username, IPEndPoint ipEndPoint, int token, CancellationToken cancellationToken)
        {
            using (var directCts = new CancellationTokenSource())
            using (var directLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, directCts.Token))
            using (var indirectCts = new CancellationTokenSource())
            using (var indirectLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, indirectCts.Token))
            {
                Diagnostic.Debug($"Attempting simultaneous direct and indirect transfer connections to {username} ({ipEndPoint})");

                var direct = GetTransferConnectionOutboundDirectAsync(ipEndPoint, token, directLinkedCts.Token);
                var indirect = GetTransferConnectionOutboundIndirectAsync(username, token, indirectLinkedCts.Token);

                var tasks = new[] { direct, indirect }.ToList();
                Task<IConnection> task;

                do
                {
                    task = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(task);
                }
                while (task.Status != TaskStatus.RanToCompletion && tasks.Count > 0);

                if (task.Status != TaskStatus.RanToCompletion)
                {
                    throw new ConnectionException($"Failed to establish a direct or indirect transfer connection to {username} ({ipEndPoint})");
                }

                var connection = await task.ConfigureAwait(false);
                var isDirect = task == direct;

                Diagnostic.Debug($"{(isDirect ? "Direct" : "Indirect")} transfer connection to {username} ({ipEndPoint}) established first, attempting to cancel {(isDirect ? "indirect" : "direct")} connection.");
                (isDirect ? indirectCts : directCts).Cancel();

                if (isDirect)
                {
                    var request = new PeerInit(SoulseekClient.Username, Constants.ConnectionType.Transfer, token).ToByteArray();
                    await connection.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                }

                await connection.WriteAsync(BitConverter.GetBytes(token), cancellationToken).ConfigureAwait(false);

                Diagnostic.Debug($"Transfer connection to {username} ({ipEndPoint}) established. (type: {connection.Type}, id: {connection.Id})");
                return connection;
            }
        }

        /// <summary>
        ///     Removes and disposes all active and queued connections.
        /// </summary>
        public async void RemoveAndDisposeAll()
        {
            PendingSolicitationDictionary.Clear();

            while (!MessageConnectionDictionary.IsEmpty)
            {
                if (MessageConnectionDictionary.TryRemove(MessageConnectionDictionary.Keys.First(), out var connection))
                {
                    (await connection.Value.ConfigureAwait(false))?.Dispose();
                }
            }
        }

        private void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing)
                {
                    RemoveAndDisposeAll();
                }

                Disposed = true;
            }
        }

        private async Task<IMessageConnection> GetMessageConnectionOutboundDirectAsync(string username, IPEndPoint ipEndPoint, CancellationToken cancellationToken)
        {
            Diagnostic.Debug($"Attempting direct message connection to {username} ({ipEndPoint})");

            var connection = ConnectionFactory.GetMessageConnection(
                username,
                ipEndPoint,
                SoulseekClient.Options.PeerConnectionOptions);

            connection.Type = ConnectionTypes.Outbound | ConnectionTypes.Direct;
            connection.MessageRead += SoulseekClient.PeerMessageHandler.HandleMessageRead;
            connection.MessageReceived += SoulseekClient.PeerMessageHandler.HandleMessageReceived;

            try
            {
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            Diagnostic.Debug($"Direct message connection to {username} ({ipEndPoint}) established. (type: {connection.Type}, id: {connection.Id})");
            return connection;
        }

        private async Task<IMessageConnection> GetMessageConnectionOutboundIndirectAsync(string username, CancellationToken cancellationToken)
        {
            var solicitationToken = SoulseekClient.GetNextToken();

            Diagnostic.Debug($"Soliciting indirect message connection to {username} with token {solicitationToken}");

            try
            {
                PendingSolicitationDictionary.TryAdd(solicitationToken, username);

                await SoulseekClient.ServerConnection
                    .WriteAsync(new ConnectToPeerRequest(solicitationToken, username, Constants.ConnectionType.Peer).ToByteArray(), cancellationToken)
                    .ConfigureAwait(false);

                using (var incomingConnection = await SoulseekClient.Waiter
                    .Wait<IConnection>(new WaitKey(Constants.WaitKey.SolicitedPeerConnection, username, solicitationToken), null, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var connection = ConnectionFactory.GetMessageConnection(
                        username,
                        incomingConnection.IPEndPoint,
                        SoulseekClient.Options.PeerConnectionOptions,
                        incomingConnection.HandoffTcpClient());

                    Diagnostic.Debug($"Indirect message connection to {username} ({incomingConnection.IPEndPoint}) handed off. (old: {incomingConnection.Id}, new: {connection.Id})");

                    connection.Type = ConnectionTypes.Outbound | ConnectionTypes.Indirect;
                    connection.MessageRead += SoulseekClient.PeerMessageHandler.HandleMessageRead;
                    connection.MessageReceived += SoulseekClient.PeerMessageHandler.HandleMessageReceived;

                    Diagnostic.Debug($"Indirect message connection to {username} ({connection.IPEndPoint}) established. (type: {connection.Type}, id: {connection.Id})");
                    return connection;
                }
            }
            finally
            {
                PendingSolicitationDictionary.TryRemove(solicitationToken, out _);
            }
        }

        private async Task<IConnection> GetTransferConnectionOutboundDirectAsync(IPEndPoint ipEndPoint, int token, CancellationToken cancellationToken)
        {
            Diagnostic.Debug($"Attempting direct transfer connection for token {token} to {ipEndPoint}");

            var connection = ConnectionFactory.GetConnection(ipEndPoint, SoulseekClient.Options.TransferConnectionOptions);

            connection.Type = ConnectionTypes.Outbound | ConnectionTypes.Direct;
            connection.Disconnected += (sender, e) => Diagnostic.Debug($"Transfer connection for token {token} to {ipEndPoint} disconnected. (type: {connection.Type}, id: {connection.Id})");

            try
            {
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            Diagnostic.Debug($"Direct transfer connection for {token} to {connection.IPEndPoint} established. (type: {connection.Type}, id: {connection.Id})");
            return connection;
        }

        private async Task<IConnection> GetTransferConnectionOutboundIndirectAsync(string username, int token, CancellationToken cancellationToken)
        {
            Diagnostic.Debug($"Soliciting indirect message connection to {username} with token {token}");

            var solicitationToken = SoulseekClient.GetNextToken();

            try
            {
                PendingSolicitationDictionary.TryAdd(solicitationToken, username);

                await SoulseekClient.ServerConnection
                    .WriteAsync(new ConnectToPeerRequest(solicitationToken, username, Constants.ConnectionType.Transfer).ToByteArray(), cancellationToken)
                    .ConfigureAwait(false);

                using (var incomingConnection = await SoulseekClient.Waiter
                    .Wait<IConnection>(new WaitKey(Constants.WaitKey.SolicitedPeerConnection, username, solicitationToken), null, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var connection = ConnectionFactory.GetConnection(
                        incomingConnection.IPEndPoint,
                        SoulseekClient.Options.TransferConnectionOptions,
                        incomingConnection.HandoffTcpClient());

                    Diagnostic.Debug($"Indirect transfer connection to {username} ({incomingConnection.IPEndPoint}) handed off. (old: {incomingConnection.Id}, new: {connection.Id})");

                    connection.Type = ConnectionTypes.Outbound | ConnectionTypes.Inbound;
                    connection.Disconnected += (sender, e) => Diagnostic.Debug($"Transfer connection for token {token} ({incomingConnection.IPEndPoint}) disconnected. (type: {connection.Type}, id: {connection.Id})");

                    Diagnostic.Debug($"Indirect transfer connection for {token} ({connection.IPEndPoint}) established. (type: {connection.Type}, id: {connection.Id})");
                    return connection;
                }
            }
            finally
            {
                PendingSolicitationDictionary.TryRemove(solicitationToken, out var _);
            }
        }

        private void MessageConnection_Disconnected(object sender, ConnectionDisconnectedEventArgs e)
        {
            var connection = (IMessageConnection)sender;

            Diagnostic.Debug($"Message connection to {connection.Username} ({connection.IPEndPoint}) disconnected. (type: {connection.Type}, id: {connection.Id})");

            TryRemoveMessageConnectionRecord(connection);
            connection.Dispose();
        }

        private void TryRemoveMessageConnectionRecord(IMessageConnection connection)
        {
            if (MessageConnectionDictionary.TryRemove(connection.Username, out var cachedConnection))
            {
                Diagnostic.Debug($"Removed message connection record for {connection.Key.Username} ({connection.IPEndPoint}) (type: {connection.Type}, id: {connection.Id})");
            }

            Diagnostic.Debug($"Message connection cache now contains {MessageConnectionDictionary.Count} connections.");
        }
    }
}