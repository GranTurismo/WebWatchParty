using Microsoft.AspNetCore.SignalR;
using WebWatchParty.Services;

namespace WebWatchParty.Hubs
{
    public class WatchHub : Hub
    {
        private readonly RoomService _roomService;

        public WatchHub(RoomService roomService)
        {
            _roomService = roomService;
        }

        public async Task JoinRoom(string roomId, string userName)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            
            var room = _roomService.GetOrCreateRoom(roomId);
            
            if (string.IsNullOrEmpty(room.AdminConnectionId))
            {
                room.AdminConnectionId = Context.ConnectionId;
            }

            // Send existing state to the new user, including if they are admin
            await Clients.Caller.SendAsync("InitRoom", room, Context.ConnectionId == room.AdminConnectionId);
            
            await Clients.Group(roomId).SendAsync("UserJoined", userName);
        }

        public async Task SendMessage(string roomId, string userName, string message)
        {
            _roomService.AddMessage(roomId, userName, message);
            await Clients.Group(roomId).SendAsync("ReceiveMessage", userName, message);
        }

        public async Task ChangeVideo(string roomId, string url)
        {
            var room = _roomService.GetOrCreateRoom(roomId);
            if (room.AdminConnectionId != Context.ConnectionId) return;

            _roomService.UpdateVideo(roomId, url);
            await Clients.Group(roomId).SendAsync("VideoChanged", url);
        }

        public async Task SyncVideo(string roomId, string action, double time)
        {
            var room = _roomService.GetOrCreateRoom(roomId);
            if (room.AdminConnectionId != Context.ConnectionId) return;

            _roomService.UpdatePlayback(roomId, action, time);
            await Clients.GroupExcept(roomId, Context.ConnectionId).SendAsync("VideoSynced", action, time);
        }
    }
}

