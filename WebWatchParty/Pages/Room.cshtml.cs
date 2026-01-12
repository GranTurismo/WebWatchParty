using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebWatchParty.Services;

namespace WebWatchParty.Pages
{
    public class RoomModel : PageModel
    {
        private readonly RoomService _roomService;

        public RoomModel(RoomService roomService)
        {
            _roomService = roomService;
        }

        [BindProperty(SupportsGet = true)]
        public string RoomId { get; set; } = string.Empty;

        [BindProperty(SupportsGet = true)]
        public string UserName { get; set; } = string.Empty;

        [BindProperty(SupportsGet = true)]
        public string Password { get; set; } = string.Empty;

        public IActionResult OnGet()
        {
            if (string.IsNullOrEmpty(RoomId) || string.IsNullOrEmpty(UserName))
            {
                return RedirectToPage("/Index");
            }

            var room = _roomService.GetOrCreateRoom(RoomId, Password);
            if (!room.IsPublic && room.Password != Password)
            {
                // Simple password check. If it's a new room, it will use the provided password.
                // If it's an existing room and user didn't provide correct password, they are rejected.
                return RedirectToPage("/Index", new { error = "Invalid password for room: " + RoomId });
            }

            return Page();
        }
    }
}
