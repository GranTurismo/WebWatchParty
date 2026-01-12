using System.Collections.Concurrent;

namespace WebWatchParty.Services
{
    public class RoomState
    {
        public string AdminConnectionId { get; set; } = string.Empty;
        public string CurrentVideoUrl { get; set; } = string.Empty;
        public string Action { get; set; } = "pause";
        public double CurrentTime { get; set; } = 0;
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        public string Password { get; set; } = string.Empty;
        public bool IsPublic { get; set; } = true;
    }

    public class ChatMessage
    {
        public string User { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class RoomService
    {
        private readonly ConcurrentDictionary<string, RoomState> _rooms = new ConcurrentDictionary<string, RoomState>();

        public RoomState GetOrCreateRoom(string roomId, string password = "")
        {
            return _rooms.GetOrAdd(roomId, id => new RoomState 
            { 
                Password = password,
                IsPublic = string.IsNullOrEmpty(password)
            });
        }

        public IEnumerable<(string Id, RoomState State)> GetRooms()
        {
            return _rooms.Select(kvp => (kvp.Key, kvp.Value));
        }

        public void UpdateVideo(string roomId, string url)
        {
            var room = GetOrCreateRoom(roomId);
            room.CurrentVideoUrl = url;
            room.Action = "pause";
            room.CurrentTime = 0;
        }

        public void UpdatePlayback(string roomId, string action, double time)
        {
            var room = GetOrCreateRoom(roomId);
            room.Action = action;
            room.CurrentTime = time;
        }

        public void AddMessage(string roomId, string user, string message)
        {
            var room = GetOrCreateRoom(roomId);
            room.Messages.Add(new ChatMessage { User = user, Message = message });
            if (room.Messages.Count > 100) room.Messages.RemoveAt(0);
        }
    }
}
