using System;
using System.Linq;
using System;
using System.Linq;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using YOUVI.RelayServer.Models;
using System.Text.Json;

namespace YOUVI.RelayServer.Hubs
{
    public class RelayHub : Hub
    {
        private readonly Microsoft.Extensions.Logging.ILogger<RelayHub>? _logger;
        // clientId -> set of connectionIds
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ClientConnections =
            new();

        // connectionId -> clientId
        private static readonly ConcurrentDictionary<string, string> ConnectionToClient =
            new();

        // pending messages for clients not currently connected (clientId -> queue of envelopes)
        private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> PendingMessages = new();

        private readonly YOUVI.RelayServer.Models.RelaySettings _settings;

        public RelayHub(Microsoft.Extensions.Logging.ILogger<RelayHub> logger, YOUVI.RelayServer.Models.RelaySettings settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public async Task Register(string clientId)
        {
            var connId = Context.ConnectionId;
            var remoteIp = Context.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = Context.GetHttpContext()?.Request?.Headers["User-Agent"].ToString() ?? string.Empty;

            if (!string.IsNullOrEmpty(_settings?.HubToken))
            {
                var tokenFromQuery = Context.GetHttpContext()?.Request?.Query["access_token"].ToString();
                var authHeader = Context.GetHttpContext()?.Request?.Headers["Authorization"].ToString();
                string? provided = null;
                if (!string.IsNullOrEmpty(tokenFromQuery)) provided = tokenFromQuery;
                else if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) provided = authHeader.Substring("Bearer ".Length);
                if (provided != _settings.HubToken)
                {
                    _logger?.LogWarning("Register rejected for {ClientId} due to invalid token (RemoteIP={RemoteIP}).", clientId, remoteIp);
                    Context.Abort();
                    return;
                }
            }

            var set = ClientConnections.GetOrAdd(clientId, _ => new ConcurrentDictionary<string, byte>());
            var wasFirst = set.IsEmpty;
            set[connId] = 0;
            ConnectionToClient[connId] = clientId;
            _logger?.LogInformation("Register: clientId {ClientId} connection {ConnId} from {RemoteIP} UA:{UserAgent}", clientId, connId, remoteIp, userAgent);

            // Notify other hub clients (e.g., bridge peers)
            try
            {
                if (wasFirst)
                {
                    await Clients.Others.SendAsync("ClientRegistered", clientId);
                    _logger?.LogInformation("Register: Notified other hub clients about registration of {ClientId}", clientId);

                    var registerMsg = new { type = "register", clientId = clientId };
                    var registerJson = JsonSerializer.Serialize(registerMsg);
                    var regEnvelope = JsonSerializer.Serialize(new { from = clientId, payload = registerJson });
                    await Clients.Others.SendAsync("ReceiveMessage", regEnvelope);
                    _logger?.LogInformation("Register: Broadcasted register envelope for {ClientId}", clientId);
                }
                else
                {
                    _logger?.LogDebug("Register: client {ClientId} connected additional connection {ConnId}; skipping broadcast.", clientId, connId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Register: failed to notify others about {ClientId}: {Message}", clientId, ex.Message);
            }

            // Flush queued pending messages for this client (if any)
            try
            {
                if (PendingMessages.TryRemove(clientId, out var queue))
                {
                    var connectionIds = set.Keys.ToList();
                    var flushed = 0;
                    while (queue.TryDequeue(out var msg))
                    {
                        await Clients.Clients(connectionIds).SendAsync("ReceiveMessage", msg);
                        flushed++;
                    }
                   if (flushed > 0)
                        _logger?.LogInformation("Register: Flushed {Count} queued messages to {ClientId}", flushed, clientId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Register: failed to flush pending messages to {ClientId}: {Message}", clientId, ex.Message);
            }
        }

        public async Task SendTo(string targetClientId, string message)
        {
            var from = ConnectionToClient.TryGetValue(Context.ConnectionId, out var f) ? f : Context.ConnectionId;

            // Ignore self-target sends
            if (!string.IsNullOrEmpty(from) && string.Equals(from, targetClientId, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("SendTo: Ignoring self-target send from {From} to {Target}", from, targetClientId);
                return;
            }

            if (ClientConnections.TryGetValue(targetClientId, out var set) && !set.IsEmpty)
            {
                var connectionIds = set.Keys.ToList();

                if (connectionIds.Count == 1 && connectionIds.Contains(Context.ConnectionId))
                {
                    _logger?.LogWarning("SendTo: target {Target} only contains caller connection {Conn}, ignoring self-send.", targetClientId, Context.ConnectionId);
                    return;
                }

                var envelope = JsonSerializer.Serialize(new { from, payload = message });
                _logger?.LogInformation("SendTo: from {From} to {Target} via connections {Count}", from, targetClientId, connectionIds.Count);
                await Clients.Clients(connectionIds).SendAsync("ReceiveMessage", envelope);
            }
            else
            {
                _logger?.LogWarning("SendTo: target {Target} not found", targetClientId);
                await Clients.Caller.SendAsync("ClientNotFound", targetClientId);
            }
        }

        public async Task JoinCall(string callId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, callId);
            var from = ConnectionToClient.TryGetValue(Context.ConnectionId, out var f) ? f : Context.ConnectionId;
            var bridgeConnectionIds = ClientConnections
                    .Where(kvp => kvp.Key.StartsWith("roomHost_", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kvp => kvp.Value.Keys)
                    .Distinct()
                    .ToList();
            if (bridgeConnectionIds.Count > 0)
            {
                _logger?.LogInformation("Host opens Waitingroom and wait for parcipiants: connection.");
            }
            else
            {
                _logger?.LogInformation("JoinCall: connection {ConnId} (client {From}) joined group {CallId}. No bridge connections to notify.", Context.ConnectionId, from, callId);
                var payload = JsonSerializer.Serialize(new { type = "participantJoined", from, callId });
                var envelope = JsonSerializer.Serialize(new { from, payload = payload });
                await Clients.Group(callId).SendAsync("ReceiveMessage", envelope);
            }

            // Also proactively notify bridge clients (clientIds starting with 'roomHost_') so the bridge won't miss the event
            // if it hasn't joined the group yet (race condition when mobile joins before bridge).
            //try
            //{
            //    var bridgeConnectionIds = ClientConnections
            //        .Where(kvp => kvp.Key.StartsWith("roomHost_", StringComparison.OrdinalIgnoreCase))
            //        .SelectMany(kvp => kvp.Value.Keys)
            //        .Distinct()
            //        .ToList();

            //    if (bridgeConnectionIds.Count > 0)
            //    {
            //        await Clients.Clients(bridgeConnectionIds).SendAsync("ReceiveMessage", envelope);
            //        _logger?.LogInformation("JoinCall: Notified {Count} bridge connections about participant {From} in call {CallId}", bridgeConnectionIds.Count, from, callId);
            //    }
            //}
            //catch (Exception ex)
            //{
            //    _logger?.LogWarning("JoinCall: failed to notify bridge clients: {Message}", ex.Message);
            //}
        }

        public async Task LeaveCall(string callId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, callId);
            var from = ConnectionToClient.TryGetValue(Context.ConnectionId, out var f) ? f : Context.ConnectionId;
            _logger?.LogInformation("LeaveCall: connection {ConnId} (client {From}) left group {CallId}", Context.ConnectionId, from, callId);
            var payload = JsonSerializer.Serialize(new { type = "participantLeft", from, callId });
            var envelope = JsonSerializer.Serialize(new { from, payload = payload });
            await Clients.Group(callId).SendAsync("ReceiveMessage", envelope);
        }

        public Task SendToGroup(string callId, string message)
        {
            var from = ConnectionToClient.TryGetValue(Context.ConnectionId, out var f) ? f : Context.ConnectionId;
            _logger?.LogInformation("SendToGroup: from {From} to group {CallId}", from, callId);
            var envelope = JsonSerializer.Serialize(new { from, payload = message });
            return Clients.Group(callId).SendAsync("ReceiveMessage", envelope);
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            var connId = Context.ConnectionId;
            var remoteIp = Context.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = Context.GetHttpContext()?.Request?.Headers["User-Agent"].ToString() ?? string.Empty;
            if (ConnectionToClient.TryRemove(connId, out var clientId))
            {
                _logger?.LogInformation("OnDisconnected: connection {ConnId} removed for client {ClientId}. RemoteIP={RemoteIP} UA={UserAgent} Exception={Exception}", connId, clientId, remoteIp, userAgent, exception?.ToString());
                if (ClientConnections.TryGetValue(clientId, out var set))
                {
                    set.TryRemove(connId, out _);
                    if (set.IsEmpty)
                    {
                        ClientConnections.TryRemove(clientId, out _);
                        _logger?.LogInformation("OnDisconnected: client {ClientId} has no more connections, removed", clientId);
                    }
                }
            }
            else
            {
                _logger?.LogInformation("OnDisconnected: connection {ConnId} had no mapped client. RemoteIP={RemoteIP} UA={UserAgent} Exception={Exception}", connId, remoteIp, userAgent, exception?.ToString());
            }

            return base.OnDisconnectedAsync(exception);
        }
    }
}
