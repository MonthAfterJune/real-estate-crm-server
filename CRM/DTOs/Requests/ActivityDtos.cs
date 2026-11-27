using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CRM.DTOs.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class ActivityCreateRequest : IValidatableObject
{
    [Required, MaxLength(50), AllowedValues("Call", "Email", "Meeting", "Viewing", "Task")]
    public string Type { get; set; } = string.Empty;
    [Required, MaxLength(200)]
    public string Subject { get; set; } = string.Empty;
    public string? Details { get; set; }
    [Range(1, int.MaxValue)]
    public int ContactId { get; set; }
    [Range(1, int.MaxValue)]
    public int? OpportunityId { get; set; }
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }
    [Range(1, int.MaxValue)]
    public int UserId { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ScheduledAt is null && CompletedAt is null)
            yield return new ValidationResult("Choose a scheduled or completed time.", [nameof(ScheduledAt), nameof(CompletedAt)]);
        if (ScheduledAt == default(DateTime) || CompletedAt == default(DateTime))
            yield return new ValidationResult("Choose a valid time.", [nameof(ScheduledAt), nameof(CompletedAt)]);
        if (ScheduledAt is { Kind: not DateTimeKind.Utc } || CompletedAt is { Kind: not DateTimeKind.Utc })
            yield return new ValidationResult("Use UTC timestamps (ending in Z).", [nameof(ScheduledAt), nameof(CompletedAt)]);
        if (Type == "Viewing" && OpportunityId is null)
            yield return new ValidationResult("A Viewing requires an opportunity.", [nameof(OpportunityId)]);
    }
}

public class ActivityUpdateRequest : ActivityCreateRequest { }
