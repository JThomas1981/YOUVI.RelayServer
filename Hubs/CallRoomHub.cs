using System.Collections.Concurrent;
using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace YOUVI.RelayServer.Hubs
{
    public class CallRoomHub : Hub
    {
        private readonly ILogger<CallRoomHub> _logger;
        private sealed class PendingMessage
        {
            public string From { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public string? Payload { get; init; }
        }

        private static readonly ConcurrentDictionary<string, ConcurrentQueue<PendingMessage>> PendingMessages = new();
        // ConnectionId → roomId (nur Hosts)
        private static readonly ConcurrentDictionary<string, string> HostRooms = new();

        // ConnectionId → roomId (Teilnehmer)
        private static readonly ConcurrentDictionary<string, string> ParticipantRooms = new();

        public CallRoomHub(ILogger<CallRoomHub> logger)
        {
            _logger = logger;
        }

        public async Task JoinRoom(string roomId, bool isHost)
        {
            _logger.LogInformation(
                "JoinRoom called: ConnectionId={ConnectionId}, RoomId={RoomId}, IsHost={IsHost}",
                Context.ConnectionId, roomId, isHost
            );

            if (isHost)
            {
                HostRooms[Context.ConnectionId] = roomId;
                _logger.LogInformation(
                    "Host registered: ConnectionId={ConnectionId} is now host of RoomId={RoomId}",
                    Context.ConnectionId, roomId
                );

                // Wenn es gepufferte Nachrichten für diesen Raum gibt, an den neu verbundenen Host liefern
                if (PendingMessages.TryRemove(roomId, out var queued))
                {
                    _logger.LogInformation("Delivering {Count} queued messages to new host {ConnectionId} for RoomId={RoomId}", queued.Count, Context.ConnectionId, roomId);
                    while (queued.TryDequeue(out var pm))
                    {
                        try
                        {
                            await Clients.Client(Context.ConnectionId).SendAsync("RoomMessage", new
                            {
                                From = pm.From,
                                Type = pm.Type,
                                Payload = pm.Payload
                            });
                            _logger.LogDebug("Delivered queued message type={Type} from {From} to new host {ConnectionId}", pm.Type, pm.From, Context.ConnectionId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed delivering queued message type={Type} from {From} to host {ConnectionId}", pm.Type, pm.From, Context.ConnectionId);
                        }
                    }
                }
            }
            else
            {
                ParticipantRooms[Context.ConnectionId] = roomId;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

            _logger.LogInformation(
                "Client joined group: ConnectionId={ConnectionId}, RoomId={RoomId}",
                Context.ConnectionId, roomId
            );

            // Informiere nur die Hosts dieses Raums über neuen Teilnehmer
            var hostIds = HostRooms.Where(kv => kv.Value == roomId).Select(kv => kv.Key).ToArray();
            if (hostIds.Length > 0)
            {
                await Clients.Clients(hostIds).SendAsync("ParticipantJoined", Context.ConnectionId);
            }
        }

        public async Task LeaveRoom(string roomId)
        {
            _logger.LogInformation(
                "LeaveRoom called: ConnectionId={ConnectionId}, RoomId={RoomId}",
                Context.ConnectionId, roomId
            );

            // Wenn der Caller ein Host ist, entferne die Host-Registrierung (erlauben das Verlassen)
            if (HostRooms.TryGetValue(Context.ConnectionId, out var hostRoom) &&
                hostRoom == roomId)
            {
                // Remove host entry so subsequent rooms can be created cleanly
                HostRooms.TryRemove(Context.ConnectionId, out _);
                _logger.LogInformation(
                    "Host leaving: ConnectionId={ConnectionId} removed as host for RoomId={RoomId}",
                    Context.ConnectionId, roomId
                );

                // Remove from group as well
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);

                // Informiere verbleibende Hosts (falls vorhanden) oder Teilnehmer
                var remainingHostIds = HostRooms.Where(kv => kv.Value == roomId).Select(kv => kv.Key).ToArray();
                if (remainingHostIds.Length > 0)
                {
                    await Clients.Clients(remainingHostIds).SendAsync("ParticipantLeft", Context.ConnectionId);
                }

                return;
            }

            // Teilnehmer leave: normal verarbeiten
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);

            ParticipantRooms.TryRemove(Context.ConnectionId, out _);

            _logger.LogInformation(
                "Client left group: ConnectionId={ConnectionId}, RoomId={RoomId}",
                Context.ConnectionId, roomId
            );

            // Informiere nur die Hosts dieses Raums über Teilnehmer verlassen
            var hostIds2 = HostRooms.Where(kv => kv.Value == roomId).Select(kv => kv.Key).ToArray();
            if (hostIds2.Length > 0)
            {
                await Clients.Clients(hostIds2).SendAsync("ParticipantLeft", Context.ConnectionId);
            }
        }

        public async Task SendRoomMessage(string roomId, string type, string payload)
        {
            _logger.LogInformation(
                "SendRoomMessage: From={ConnectionId}, RoomId={RoomId}, Type={Type}, PayloadLength={PayloadLength}",
                Context.ConnectionId, roomId, type, payload?.Length ?? 0
            );

            // Detailliertes Payload-Logging (wenn JSON: pretty-print, sonst raw)
            if (!string.IsNullOrEmpty(payload))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(payload);
                    var pretty = System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    _logger.LogInformation("SendRoomMessage payload (JSON, pretty):\n{Payload}", pretty);
                }
                catch (System.Text.Json.JsonException)
                {
                    _logger.LogInformation("SendRoomMessage payload (raw): {Payload}", payload);
                }
            }
            else
            {
                _logger.LogInformation("SendRoomMessage payload: <empty>");
            }

            var isSenderHost = HostRooms.TryGetValue(Context.ConnectionId, out var hostRoom) && hostRoom == roomId;

            if (isSenderHost)
            {
                // Host -> broadcast an Teilnehmer des Raums, Hosts ausgeschlossen
                var hostConnectionIds = HostRooms
                    .Where(kv => kv.Value == roomId)
                    .Select(kv => kv.Key)
                    .ToArray();

                _logger.LogDebug("Host message: broadcasting to group except hosts. RoomId={RoomId}, HostCount={HostCount}", roomId, hostConnectionIds.Length);

                try
                {
                    await Clients.GroupExcept(roomId, hostConnectionIds).SendAsync("RoomMessage", new
                    {
                        From = Context.ConnectionId,
                        Type = type,
                        Payload = payload
                    });

                    _logger.LogInformation("Host broadcast successful to group except hosts. RoomId={RoomId}", roomId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GroupExcept failed, falling back to per-participant sends. RoomId={RoomId}", roomId);

                    var participantIds = ParticipantRooms.Where(kv => kv.Value == roomId).Select(kv => kv.Key).ToArray();
                    _logger.LogInformation("Fallback: sending to {ParticipantCount} participants individually.", participantIds.Length);

                    foreach (var pid in participantIds)
                    {
                        try
                        {
                            await Clients.Client(pid).SendAsync("RoomMessage", new
                            {
                                From = Context.ConnectionId,
                                Type = type,
                                Payload = payload
                            });

                            _logger.LogInformation("Sent RoomMessage to participant {ParticipantId} (RoomId={RoomId})", pid, roomId);
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogWarning(ex2, "Failed to send RoomMessage to participant {ParticipantId}", pid);
                        }
                    }
                }
            }
            else
            {
                // Teilnehmer -> Nachricht nur an Host(s)
                var hostConnectionIds = HostRooms
                    .Where(kv => kv.Value == roomId)
                    .Select(kv => kv.Key)
                    .ToArray();

                if (hostConnectionIds.Length == 0)
                {
                    // Kein Host aktuell im Raum -> Nachricht puffern statt verwerfen
                    var queue = PendingMessages.GetOrAdd(roomId, _ => new System.Collections.Concurrent.ConcurrentQueue<PendingMessage>());
                    queue.Enqueue(new PendingMessage { From = Context.ConnectionId, Type = type ?? string.Empty, Payload = payload });
                    _logger.LogInformation("No host registered for RoomId={RoomId}. Queued message of type={Type} from {ConnectionId} (queuedCount={Count})", roomId, type, Context.ConnectionId, queue.Count);
                    return;
                }

                _logger.LogDebug("Client message: forwarding to {HostCount} host(s) for RoomId={RoomId}", hostConnectionIds.Length, roomId);

                foreach (var hostConn in hostConnectionIds)
                {
                    try
                    {
                        await Clients.Client(hostConn).SendAsync("RoomMessage", new
                        {
                            From = Context.ConnectionId,
                            Type = type,
                            Payload = payload
                        });

                        _logger.LogInformation("Forwarded RoomMessage to host {HostConnectionId} (RoomId={RoomId})", hostConn, roomId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to forward RoomMessage to host {HostConnectionId}", hostConn);
                    }
                }
            }
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            _logger.LogInformation(
                "OnDisconnectedAsync: ConnectionId={ConnectionId}, Exception={Exception}",
                Context.ConnectionId, ex?.Message
            );

            // Wenn der disconnectende Client ein Host ist, Host-Registrierung entfernen
            if (HostRooms.TryRemove(Context.ConnectionId, out var roomId))
            {
                _logger.LogInformation(
                    "Host disconnected and removed: ConnectionId={ConnectionId}, RoomId={RoomId}",
                    Context.ConnectionId, roomId
                );

                // Informiere verbleibende Hosts über den disconnect (optional)
                var hostIds = HostRooms.Where(kv => kv.Value == roomId).Select(kv => kv.Key).ToArray();
                if (hostIds.Length > 0)
                {
                    foreach (var hostId in hostIds)
                    {
                        await Clients.Client(hostId).SendAsync("ParticipantLeft", Context.ConnectionId);
                    }
                }
            }
            else
            {
                // Teilnehmer: nur die Hosts des betroffenen Raums informieren
                if (ParticipantRooms.TryRemove(Context.ConnectionId, out var participantRoomId))
                {
                    var hostIds = HostRooms.Where(kv => kv.Value == participantRoomId).Select(kv => kv.Key).ToArray();
                    foreach (var hostId in hostIds)
                    {
                        await Clients.Client(hostId).SendAsync("ParticipantLeft", Context.ConnectionId);
                    }
                }
            }

            await base.OnDisconnectedAsync(ex);
        }
    }
}