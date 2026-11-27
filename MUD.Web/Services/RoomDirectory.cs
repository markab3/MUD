using MUD.Contracts;

namespace MUD.Web.Services;

// The rooms the autocomplete matches against: ListRooms once per circuit (scoped), shared by every key input on the page, and
// loaded again after Invalidate() (pages call it when they load). Typing never calls core. A failed load is not kept, so the next
// use asks again; callers then get null ("unknown") and show no unknown-key message.
public sealed class RoomDirectory
{
    private readonly IAdminClient _core;
    private Task<IReadOnlyList<RoomRecord>?>? _load;

    public RoomDirectory(IAdminClient core) { _core = core; }

    public void Invalidate() => _load = null;

    public async Task<IReadOnlyList<RoomRecord>?> GetAsync(string token)
    {
        var load = _load ??= LoadAsync(token);
        var rooms = await load;
        if (rooms == null && ReferenceEquals(_load, load)) { _load = null; }
        return rooms;
    }

    private async Task<IReadOnlyList<RoomRecord>?> LoadAsync(string token)
    {
        try
        {
            var rooms = (await _core.ListRoomsAsync(token)).Select(RoomRecord.From).ToList();
            return rooms;
        }
        catch (CoreUnavailableException) { return null; }
        catch (CoreRefusedException) { return null; }
    }
}
