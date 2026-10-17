using System.ComponentModel.DataAnnotations;

namespace MUD.Web.Services;

/// <summary>One exit row of the room form: direction plus the full key of the target room.</summary>
public class ExitInput
{
    [StringLength(32)]
    [RegularExpression(@"^[A-Za-z][A-Za-z ]*$", ErrorMessage = "Direction may only contain letters and spaces.")]
    public string? Dir { get; set; }

    [StringLength(128)]
    [RegularExpression(@"^[a-z0-9_]+(\.[a-z0-9_]+)*$", ErrorMessage = "Target must be a room key such as area.room.")]
    public string? To { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Dir) && string.IsNullOrWhiteSpace(To);
}

/// <summary>
/// Room creation form. Field names follow specs/ROOM_FILES.md (key, short, long, area, exits dir/to).
/// </summary>
public class RoomForm : IValidatableObject
{
    public const int ExitRows = 4;

    [Required, StringLength(64)]
    [RegularExpression("^[a-z0-9_]+$", ErrorMessage = "Key may only contain lowercase letters, digits and underscores.")]
    [Display(Name = "Room key")]
    public string? Key { get; set; }

    [Required, StringLength(100)]
    [Display(Name = "Short description")]
    public string? Short { get; set; }

    [Required, StringLength(4000)]
    [Display(Name = "Long description")]
    public string? Long { get; set; }

    [Required, StringLength(128)]
    [RegularExpression(@"^[a-z0-9_]+(\.[a-z0-9_]+)*$", ErrorMessage = "Area must be an area prefix such as tower_of_aellioc.")]
    [Display(Name = "Area prefix")]
    public string? Area { get; set; }

    public List<ExitInput> Exits { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Exits.Count > ExitRows)
        {
            yield return new ValidationResult($"At most {ExitRows} exits are supported.", new[] { nameof(Exits) });
        }
        for (var i = 0; i < Exits.Count; i++)
        {
            var e = Exits[i];
            if (e == null || e.IsEmpty) { continue; }
            if (string.IsNullOrWhiteSpace(e.Dir))
            {
                yield return new ValidationResult("Exit direction is required when a target is given.", new[] { $"Exits[{i}].Dir" });
            }
            if (string.IsNullOrWhiteSpace(e.To))
            {
                yield return new ValidationResult("Exit target is required when a direction is given.", new[] { $"Exits[{i}].To" });
            }
        }
    }
}

public enum RoomCreationStatus
{
    NotImplemented,
    Created,
}

public record RoomCreationResult(RoomCreationStatus Status);

/// <summary>Future hook for creating rooms from the website.</summary>
public interface IRoomCreationService
{
    RoomCreationResult Create(RoomForm form, string creatorName);
}

/// <summary>Placeholder: persists nothing.</summary>
public class StubRoomCreationService : IRoomCreationService
{
    public RoomCreationResult Create(RoomForm form, string creatorName) =>
        new(RoomCreationStatus.NotImplemented);
}
