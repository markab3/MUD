namespace MUD.Web.Services;

// Replaces TempData["Notice"] for the circuit (WEB_BLAZOR.md 4.1): set before a navigation, shown once by the page that follows.
// Scoped, so it lives with the circuit and is lost on a full reload.
public sealed class NoticeService
{
    private string? _notice;

    public void Set(string notice) => _notice = notice;

    public string? Take()
    {
        var n = _notice;
        _notice = null;
        return n;
    }
}
