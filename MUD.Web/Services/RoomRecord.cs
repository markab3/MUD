using MUD.Contracts;

namespace MUD.Web.Services;

public record RoomExitRecord(string Name, string To);

// Definition fields of a room that the map needs. X/Y/Z are null when the room is unplaced.
public record RoomRecord(string Key, string? Area, string Short, int? X, int? Y, int? Z, IReadOnlyList<RoomExitRecord> Exits)
{
    // From a room as core's Admin service returns it.
    public static RoomRecord From(RoomDetail room) => new(
        room.Key,
        string.IsNullOrEmpty(room.Area) ? null : room.Area,
        room.ShortDescription,
        room.HasX ? room.X : null,
        room.HasY ? room.Y : null,
        room.HasZ ? room.Z : null,
        room.Exits.Select(e => new RoomExitRecord(e.Name, e.To)).ToList());
}
