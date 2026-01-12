using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebWatchParty.Services;

namespace WebWatchParty.Pages;

public class IndexModel : PageModel
{
    private readonly RoomService _roomService;

    public IndexModel(RoomService roomService)
    {
        _roomService = roomService;
    }

    public List<(string Id, RoomState State)> Rooms { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Error { get; set; }

    public void OnGet()
    {
        Rooms = _roomService.GetRooms().ToList();
    }
}
