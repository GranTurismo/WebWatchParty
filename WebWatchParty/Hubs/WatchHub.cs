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

        public async Task JoinRoom(string roomId, string userName, string sessionId)
        {
            var joinResult = _roomService.JoinRoom(roomId, userName, sessionId, Context.ConnectionId);
            if (!joinResult.Success || joinResult.Participant is null || joinResult.Snapshot is null)
            {
                await Clients.Caller.SendAsync("JoinRejected", joinResult.Error);
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

            await Clients.Caller.SendAsync("InitRoom", joinResult.Snapshot, joinResult.IsAdmin, Context.ConnectionId);
            await Clients.Group(roomId).SendAsync("ParticipantsUpdated", joinResult.Snapshot.Participants, joinResult.Snapshot.AdminConnectionId);

            if (!joinResult.ReconnectedSession)
            {
                await Clients.GroupExcept(roomId, Context.ConnectionId).SendAsync("UserJoined", joinResult.Participant.UserName);
            }
        }

        public async Task SendMessage(string roomId, string message)
        {
            var participant = _roomService.GetParticipant(roomId, Context.ConnectionId);
            var trimmedMessage = message?.Trim();

            if (participant is null || string.IsNullOrWhiteSpace(trimmedMessage))
            {
                return;
            }

            _roomService.AddMessage(roomId, participant.UserName, trimmedMessage);
            await Clients.Group(roomId).SendAsync("ReceiveMessage", participant.UserName, trimmedMessage);
        }

        public async Task ChangeVideo(string roomId, string url)
        {
            if (!_roomService.IsAdmin(roomId, Context.ConnectionId) || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _roomService.UpdateVideo(roomId, url);
            await Clients.Group(roomId).SendAsync("VideoChanged", url);
        }

        public async Task SyncVideo(string roomId, string action, double time)
        {
            if (!_roomService.IsAdmin(roomId, Context.ConnectionId))
            {
                return;
            }

            _roomService.UpdatePlayback(roomId, action, time);
            await Clients.GroupExcept(roomId, Context.ConnectionId).SendAsync("VideoSynced", action, time);
        }

        public async Task SendVoiceOffer(string roomId, string targetConnectionId, string offerJson)
        {
            var participant = _roomService.GetParticipant(roomId, Context.ConnectionId);
            if (participant is null || _roomService.GetParticipant(roomId, targetConnectionId) is null)
            {
                return;
            }

            await Clients.Client(targetConnectionId).SendAsync("ReceiveVoiceOffer", Context.ConnectionId, participant.UserName, offerJson);
        }

        public async Task SendVoiceAnswer(string roomId, string targetConnectionId, string answerJson)
        {
            if (_roomService.GetParticipant(roomId, Context.ConnectionId) is null || _roomService.GetParticipant(roomId, targetConnectionId) is null)
            {
                return;
            }

            await Clients.Client(targetConnectionId).SendAsync("ReceiveVoiceAnswer", Context.ConnectionId, answerJson);
        }

        public async Task SendIceCandidate(string roomId, string targetConnectionId, string candidateJson)
        {
            if (_roomService.GetParticipant(roomId, Context.ConnectionId) is null || _roomService.GetParticipant(roomId, targetConnectionId) is null)
            {
                return;
            }

            await Clients.Client(targetConnectionId).SendAsync("ReceiveIceCandidate", Context.ConnectionId, candidateJson);
        }

        public async Task UpdateVoiceState(string roomId, bool voiceEnabled, bool isMuted)
        {
            var snapshot = _roomService.UpdateVoiceState(roomId, Context.ConnectionId, voiceEnabled, isMuted);
            if (snapshot is null)
            {
                return;
            }

            await Clients.Group(roomId).SendAsync("ParticipantsUpdated", snapshot.Participants, snapshot.AdminConnectionId);
        }

        public async Task KickUser(string roomId, string targetConnectionId)
        {
            var kickResult = _roomService.KickParticipant(roomId, Context.ConnectionId, targetConnectionId);
            if (!kickResult.Success || kickResult.Snapshot is null)
            {
                if (!string.IsNullOrWhiteSpace(kickResult.Error))
                {
                    await Clients.Caller.SendAsync("ModerationFailed", kickResult.Error);
                }

                return;
            }

            await Groups.RemoveFromGroupAsync(kickResult.TargetConnectionId, roomId);
            await Clients.Client(kickResult.TargetConnectionId).SendAsync("Kicked", $"{kickResult.AdminUserName} removed you from room {roomId}.");
            await Clients.Group(roomId).SendAsync("ParticipantsUpdated", kickResult.Snapshot.Participants, kickResult.Snapshot.AdminConnectionId);
            await Clients.Group(roomId).SendAsync("UserKicked", kickResult.TargetUserName, kickResult.AdminUserName);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var removalResult = _roomService.RemoveConnection(Context.ConnectionId);

            if (removalResult is not null && !removalResult.RoomRemoved && removalResult.Snapshot is not null)
            {
                await Clients.Group(removalResult.RoomId).SendAsync("UserLeft", removalResult.UserName);
                await Clients.Group(removalResult.RoomId).SendAsync("ParticipantsUpdated", removalResult.Snapshot.Participants, removalResult.Snapshot.AdminConnectionId);

                if (removalResult.AdminChanged && !string.IsNullOrWhiteSpace(removalResult.NewAdminUserName))
                {
                    await Clients.Group(removalResult.RoomId).SendAsync("AdminChanged", removalResult.NewAdminUserName);
                }
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}

