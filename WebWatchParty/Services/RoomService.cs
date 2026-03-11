using System.Collections.Concurrent;

namespace WebWatchParty.Services
{
    public class RoomParticipant
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
        public bool VoiceEnabled { get; set; }
        public bool IsMuted { get; set; }
    }

    public class ParticipantViewModel
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public bool VoiceEnabled { get; set; }
        public bool IsMuted { get; set; }
    }

    public class RoomSnapshot
    {
        public string AdminConnectionId { get; set; } = string.Empty;
        public string AdminUserName { get; set; } = string.Empty;
        public string CurrentVideoUrl { get; set; } = string.Empty;
        public string Action { get; set; } = "pause";
        public double CurrentTime { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
        public List<ParticipantViewModel> Participants { get; set; } = new();
        public bool IsPublic { get; set; } = true;
    }

    public class JoinRoomResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = string.Empty;
        public RoomParticipant? Participant { get; set; }
        public RoomSnapshot? Snapshot { get; set; }
        public bool IsAdmin { get; set; }
        public bool ReconnectedSession { get; set; }
    }

    public class RemovalResult
    {
        public string RoomId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public RoomSnapshot? Snapshot { get; set; }
        public bool RoomRemoved { get; set; }
        public bool AdminChanged { get; set; }
        public string NewAdminUserName { get; set; } = string.Empty;
    }

    public class KickResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public string AdminUserName { get; set; } = string.Empty;
        public string TargetUserName { get; set; } = string.Empty;
        public string TargetConnectionId { get; set; } = string.Empty;
        public RoomSnapshot? Snapshot { get; set; }
    }

    public class RoomState
    {
        public object SyncRoot { get; } = new();
        public string AdminConnectionId { get; set; } = string.Empty;
        public string CurrentVideoUrl { get; set; } = string.Empty;
        public string Action { get; set; } = "pause";
        public double CurrentTime { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
        public string Password { get; set; } = string.Empty;
        public bool IsPublic { get; set; } = true;
        public ConcurrentDictionary<string, RoomParticipant> Participants { get; } = new();
        public HashSet<string> KickedSessionIds { get; } = new(StringComparer.Ordinal);
    }

    public class ChatMessage
    {
        public string User { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class RoomService
    {
        private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _connectionRooms = new(StringComparer.Ordinal);

        public RoomState GetOrCreateRoom(string roomId, string password = "")
        {
            return _rooms.GetOrAdd(roomId, _ => new RoomState
            {
                Password = password,
                IsPublic = string.IsNullOrEmpty(password)
            });
        }

        public IEnumerable<(string Id, RoomState State)> GetRooms()
        {
            return _rooms
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => (kvp.Key, kvp.Value));
        }

        public JoinRoomResult JoinRoom(string roomId, string userName, string sessionId, string connectionId)
        {
            var normalizedRoomId = roomId.Trim();
            var normalizedUserName = userName.Trim();
            var normalizedSessionId = sessionId.Trim();

            if (string.IsNullOrWhiteSpace(normalizedRoomId) || string.IsNullOrWhiteSpace(normalizedUserName) || string.IsNullOrWhiteSpace(normalizedSessionId))
            {
                return new JoinRoomResult
                {
                    Success = false,
                    Error = "Room, user name, and session are required."
                };
            }

            var room = GetOrCreateRoom(normalizedRoomId);

            lock (room.SyncRoot)
            {
                if (room.KickedSessionIds.Contains(normalizedSessionId))
                {
                    return new JoinRoomResult
                    {
                        Success = false,
                        Error = "You were removed from this room by the admin."
                    };
                }

                var existingSameName = room.Participants.Values.FirstOrDefault(participant =>
                    participant.UserName.Equals(normalizedUserName, StringComparison.OrdinalIgnoreCase) &&
                    !participant.SessionId.Equals(normalizedSessionId, StringComparison.Ordinal));

                if (existingSameName is not null)
                {
                    return new JoinRoomResult
                    {
                        Success = false,
                        Error = $"The name '{normalizedUserName}' is already in use in this room."
                    };
                }

                var existingSession = room.Participants.Values.FirstOrDefault(participant =>
                    participant.SessionId.Equals(normalizedSessionId, StringComparison.Ordinal));

                var reconnectedSession = false;
                if (existingSession is not null)
                {
                    reconnectedSession = true;
                    room.Participants.TryRemove(existingSession.ConnectionId, out _);
                    _connectionRooms.TryRemove(existingSession.ConnectionId, out _);

                    if (room.AdminConnectionId == existingSession.ConnectionId)
                    {
                        room.AdminConnectionId = string.Empty;
                    }
                }

                var participant = new RoomParticipant
                {
                    ConnectionId = connectionId,
                    SessionId = normalizedSessionId,
                    UserName = normalizedUserName,
                    JoinedAtUtc = DateTime.UtcNow
                };

                room.Participants[connectionId] = participant;
                _connectionRooms[connectionId] = normalizedRoomId;

                EnsureAdmin(room);

                return new JoinRoomResult
                {
                    Success = true,
                    Participant = participant,
                    Snapshot = BuildSnapshot(room),
                    IsAdmin = room.AdminConnectionId == connectionId,
                    ReconnectedSession = reconnectedSession
                };
            }
        }

        public void UpdateVideo(string roomId, string url)
        {
            var room = GetOrCreateRoom(roomId);

            lock (room.SyncRoot)
            {
                room.CurrentVideoUrl = url;
                room.Action = "pause";
                room.CurrentTime = 0;
            }
        }

        public void UpdatePlayback(string roomId, string action, double time)
        {
            var room = GetOrCreateRoom(roomId);

            lock (room.SyncRoot)
            {
                room.Action = action;
                room.CurrentTime = time;
            }
        }

        public void AddMessage(string roomId, string user, string message)
        {
            var room = GetOrCreateRoom(roomId);

            lock (room.SyncRoot)
            {
                room.Messages.Add(new ChatMessage { User = user, Message = message });
                if (room.Messages.Count > 100)
                {
                    room.Messages.RemoveAt(0);
                }
            }
        }

        public RoomParticipant? GetParticipant(string roomId, string connectionId)
        {
            return _rooms.TryGetValue(roomId, out var room) && room.Participants.TryGetValue(connectionId, out var participant)
                ? participant
                : null;
        }

        public bool IsAdmin(string roomId, string connectionId)
        {
            return _rooms.TryGetValue(roomId, out var room) && room.AdminConnectionId == connectionId;
        }

        public RoomSnapshot? GetSnapshot(string roomId)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
            {
                return null;
            }

            lock (room.SyncRoot)
            {
                return BuildSnapshot(room);
            }
        }

        public RemovalResult? RemoveConnection(string connectionId)
        {
            if (!_connectionRooms.TryRemove(connectionId, out var roomId) || !_rooms.TryGetValue(roomId, out var room))
            {
                return null;
            }

            lock (room.SyncRoot)
            {
                if (!room.Participants.TryRemove(connectionId, out var participant))
                {
                    return null;
                }

                var previousAdminConnectionId = room.AdminConnectionId;
                EnsureAdmin(room);

                if (room.Participants.IsEmpty)
                {
                    _rooms.TryRemove(roomId, out _);
                    return new RemovalResult
                    {
                        RoomId = roomId,
                        UserName = participant.UserName,
                        RoomRemoved = true,
                        AdminChanged = previousAdminConnectionId == connectionId
                    };
                }

                var snapshot = BuildSnapshot(room);
                return new RemovalResult
                {
                    RoomId = roomId,
                    UserName = participant.UserName,
                    Snapshot = snapshot,
                    RoomRemoved = false,
                    AdminChanged = previousAdminConnectionId != snapshot.AdminConnectionId,
                    NewAdminUserName = snapshot.AdminUserName
                };
            }
        }

        public KickResult KickParticipant(string roomId, string adminConnectionId, string targetConnectionId)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
            {
                return new KickResult
                {
                    Success = false,
                    Error = "Room not found."
                };
            }

            lock (room.SyncRoot)
            {
                if (room.AdminConnectionId != adminConnectionId)
                {
                    return new KickResult
                    {
                        Success = false,
                        Error = "Only the admin can remove participants."
                    };
                }

                if (adminConnectionId == targetConnectionId)
                {
                    return new KickResult
                    {
                        Success = false,
                        Error = "The admin cannot remove themselves."
                    };
                }

                if (!room.Participants.TryRemove(targetConnectionId, out var targetParticipant))
                {
                    return new KickResult
                    {
                        Success = false,
                        Error = "Participant not found."
                    };
                }

                _connectionRooms.TryRemove(targetConnectionId, out _);
                room.KickedSessionIds.Add(targetParticipant.SessionId);

                EnsureAdmin(room);

                return new KickResult
                {
                    Success = true,
                    RoomId = roomId,
                    AdminUserName = room.Participants.TryGetValue(adminConnectionId, out var adminParticipant)
                        ? adminParticipant.UserName
                        : "Admin",
                    TargetUserName = targetParticipant.UserName,
                    TargetConnectionId = targetConnectionId,
                    Snapshot = BuildSnapshot(room)
                };
            }
        }

        public RoomSnapshot? UpdateVoiceState(string roomId, string connectionId, bool voiceEnabled, bool isMuted)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
            {
                return null;
            }

            lock (room.SyncRoot)
            {
                if (!room.Participants.TryGetValue(connectionId, out var participant))
                {
                    return null;
                }

                participant.VoiceEnabled = voiceEnabled;
                participant.IsMuted = voiceEnabled && isMuted;
                return BuildSnapshot(room);
            }
        }

        private static RoomSnapshot BuildSnapshot(RoomState room)
        {
            var participants = room.Participants.Values
                .OrderBy(participant => participant.JoinedAtUtc)
                .ThenBy(participant => participant.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(participant => new ParticipantViewModel
                {
                    ConnectionId = participant.ConnectionId,
                    UserName = participant.UserName,
                    IsAdmin = participant.ConnectionId == room.AdminConnectionId,
                    VoiceEnabled = participant.VoiceEnabled,
                    IsMuted = participant.IsMuted
                })
                .ToList();

            return new RoomSnapshot
            {
                AdminConnectionId = room.AdminConnectionId,
                AdminUserName = participants.FirstOrDefault(participant => participant.IsAdmin)?.UserName ?? string.Empty,
                CurrentVideoUrl = room.CurrentVideoUrl,
                Action = room.Action,
                CurrentTime = room.CurrentTime,
                Messages = room.Messages.Select(message => new ChatMessage
                {
                    User = message.User,
                    Message = message.Message
                }).ToList(),
                Participants = participants,
                IsPublic = room.IsPublic
            };
        }

        private static void EnsureAdmin(RoomState room)
        {
            if (!string.IsNullOrWhiteSpace(room.AdminConnectionId) && room.Participants.ContainsKey(room.AdminConnectionId))
            {
                return;
            }

            room.AdminConnectionId = room.Participants.Values
                .OrderBy(participant => participant.JoinedAtUtc)
                .Select(participant => participant.ConnectionId)
                .FirstOrDefault() ?? string.Empty;
        }
    }
}
