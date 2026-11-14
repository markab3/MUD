using MUD.Contracts;
using MUD.Data;
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

    // Blank = unplaced (null), never 0.
    [Range(-100000, 100000)] public int? X { get; set; }
    [Range(-100000, 100000)] public int? Y { get; set; }
    [Range(-100000, 100000)] public int? Z { get; set; }

    // Tick to save exits whose target room does not exist.
    public bool ConfirmDangling { get; set; }

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

public record RoomFieldError(string Field, string Message);

public enum RoomCreationStatus
{
    Created,
    Invalid,
    KeyExists,
    Forbidden,
    Failed,
}

public record RoomCreationResult(RoomCreationStatus Status, IReadOnlyList<RoomFieldError>? Errors = null, string? CreatedKey = null)
{
    public IReadOnlyList<RoomFieldError> FieldErrors => Errors ?? Array.Empty<RoomFieldError>();
}

/// <summary>Creates rooms from the website. The form's Key is the local key; the stored key is Area + "." + Key.</summary>
public interface IRoomCreationService
{
    /// <summary>Throws CoreUnavailableException when core could not answer.</summary>
    Task<RoomCreationResult> CreateAsync(RoomForm form, string token, CancellationToken cancellation = default);
}

/// <summary>Real implementation: the local key and area prefix are checked here with MUD.Data.RoomRules, then core validates the
/// whole room with the same rules, audits it and inserts it (insert-only, it never overwrites an existing room).</summary>
public class RoomCreationService : IRoomCreationService
{
    private readonly IAdminClient _core;

    public RoomCreationService(IAdminClient core)
    {
        _core = core;
    }

    public async Task<RoomCreationResult> CreateAsync(RoomForm form, string token, CancellationToken cancellation = default)
    {
        var localError = RoomRules.CheckLocalKey(form.Key ?? string.Empty);
        if (localError != null)
        {
            return new(RoomCreationStatus.Invalid, new[] { new RoomFieldError("Key", char.ToUpperInvariant(localError[0]) + localError.Substring(1)) });
        }
        var area = (form.Area ?? string.Empty).Trim();
        var areaError = RoomRules.CheckAreaPrefix(area);
        if (areaError != null)
        {
            return new(RoomCreationStatus.Invalid, new[] { new RoomFieldError("Area", char.ToUpperInvariant(areaError[0]) + areaError.Substring(1)) });
        }
        var fullKey = area + "." + form.Key!.Trim();

        var request = new RoomEditRequest
        {
            Key = fullKey,
            ShortDescription = form.Short ?? string.Empty,
            LongDescription = form.Long ?? string.Empty,
            Area = area,
            ConfirmDangling = form.ConfirmDangling,
        };
        if (form.X.HasValue) { request.X = form.X.Value; }
        if (form.Y.HasValue) { request.Y = form.Y.Value; }
        if (form.Z.HasValue) { request.Z = form.Z.Value; }
        request.Exits.AddRange(form.Exits.Where(e => e != null && !e.IsEmpty)
            .Select(e => new RoomExit { Name = (e.Dir ?? string.Empty).Trim(), To = (e.To ?? string.Empty).Trim() }));

        RoomEditReply reply;
        try
        {
            reply = await _core.CreateRoomAsync(token, request, cancellation);
        }
        catch (CoreRefusedException)
        {
            return new(RoomCreationStatus.Forbidden);
        }
        return reply.Result switch
        {
            AdminResult.Ok => new(RoomCreationStatus.Created, null, fullKey),
            AdminResult.Invalid => new(RoomCreationStatus.Invalid, reply.Errors.Select(e => new RoomFieldError(e.Field, e.Message)).ToList()),
            AdminResult.AlreadyExists => new(RoomCreationStatus.KeyExists),
            AdminResult.Forbidden => new(RoomCreationStatus.Forbidden),
            _ => new(RoomCreationStatus.Failed),
        };
    }
}
