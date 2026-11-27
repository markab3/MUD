namespace MUD.Web.Services;

public enum RoomKeyMode
{
    Existing,   // the key must be an existing room (exit targets)
    New,        // the key names a room to create: existing rooms are shown only to help avoid a duplicate, never chosen
}

// What a key press did, for the component: whether the browser's own action is cancelled is decided in app.js from the
// rendered aria attributes; Chosen is the key to hand to the page when a suggestion was picked with Enter.
public readonly record struct RoomKeyPress(string? Chosen);

// The behaviour of the room key combobox without any markup (WEB_BLAZOR.md 4.4): the text, the suggestions, the highlighted row,
// open/closed and the "no such room" problem. RoomKeyInput.razor only forwards DOM events to it.
public sealed class RoomKeyCombo
{
    private IReadOnlyList<RoomRecord>? _rooms;
    // Enter without choosing a row: the typed key is judged now, while the field still has focus.
    private bool _checkNow;

    public RoomKeyCombo(RoomKeyMode mode, string? scope = null)
    {
        Mode = mode;
        Scope = scope;
    }

    public RoomKeyMode Mode { get; }
    // New mode: the key prefix that is fixed before the typed local key ("area."); null in Existing mode.
    public string? Scope { get; set; }
    public string Text { get; private set; } = string.Empty;
    public bool Focused { get; private set; }
    public bool Open { get; private set; }
    public int Active { get; private set; } = -1;
    public IReadOnlyList<RoomRecord> Results { get; private set; } = Array.Empty<RoomRecord>();
    // null until the room list is known (or when core could not answer).
    public IReadOnlyList<RoomRecord>? Rooms
    {
        get => _rooms;
        set { _rooms = value; if (Open) { Refresh(); } }
    }

    public bool ShowList => Open && Results.Count > 0;

    // Text for the polite live region: how many rooms match while the list is open.
    public string Announcement => !Open ? string.Empty
        : Results.Count == 0 ? "No matching rooms"
        : Results.Count == 1 ? "1 matching room" : Results.Count + " matching rooms";

    // New mode: the typed local key, with the scope, is a room that exists already.
    public bool IsDuplicate => Mode == RoomKeyMode.New && !string.IsNullOrEmpty(Text.Trim())
        && RoomKeyMatcher.Exists(_rooms, (Scope ?? string.Empty) + Text.Trim());

    // The inline message for the typed key, or null. Existing mode: a key that matches no room (only when the list is known and the
    // user is not in the middle of typing). New mode: a key that is taken.
    public string? Problem
    {
        get
        {
            if (Mode == RoomKeyMode.New) { return IsDuplicate ? "A room with this key already exists. Choose another key." : null; }
            if ((Focused && !_checkNow) || _rooms == null || string.IsNullOrWhiteSpace(Text)) { return null; }
            return RoomKeyMatcher.Exists(_rooms, Text) ? null : "No room has this key.";
        }
    }

    // The page changed the value (draft restore, reset): show it, do not open the list.
    public void SetFromParent(string? text)
    {
        var t = text ?? string.Empty;
        if (t == Text) { return; }
        Text = t;
        Open = false;
        Active = -1;
        Results = Array.Empty<RoomRecord>();
    }

    // The user typed.
    public void Type(string? text)
    {
        Text = text ?? string.Empty;
        _checkNow = false;
        Open = Text.Trim().Length > 0;
        Active = -1;
        Refresh();
    }

    public void Focus() { Focused = true; }

    public void Blur() { Focused = false; _checkNow = false; Open = false; Active = -1; }

    public void Close() { Open = false; Active = -1; }

    // A tap or click on row index: Existing mode picks it, New mode ignores it (the rows only warn about taken keys).
    public string? Choose(int index)
    {
        if (Mode != RoomKeyMode.Existing || index < 0 || index >= Results.Count) { return null; }
        Text = Results[index].Key;
        Open = false;
        Active = -1;
        Results = Array.Empty<RoomRecord>();
        return Text;
    }

    public RoomKeyPress KeyDown(string? key)
    {
        switch (key)
        {
            case "ArrowDown":
                if (!Open && Text.Trim().Length > 0) { Open = true; Refresh(); }
                if (ShowList && Mode == RoomKeyMode.Existing) { Active = Active + 1 >= Results.Count ? 0 : Active + 1; }
                return default;
            case "ArrowUp":
                if (ShowList && Mode == RoomKeyMode.Existing) { Active = Active <= 0 ? Results.Count - 1 : Active - 1; }
                return default;
            case "Enter":
                // With a highlighted row Enter picks it; otherwise the typed text stands and the form's own Enter handling goes on.
                if (ShowList && Active >= 0) { return new RoomKeyPress(Choose(Active)); }
                Close();
                _checkNow = true;
                return default;
            case "Escape":
                Close();
                return default;
            default:
                return default;
        }
    }

    private void Refresh()
    {
        Results = Open ? RoomKeyMatcher.Match(_rooms, Text, RoomKeyMatcher.MaxResults, Mode == RoomKeyMode.New ? Scope : null) : Array.Empty<RoomRecord>();
        if (Active >= Results.Count) { Active = -1; }
    }
}
