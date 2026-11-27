using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CRM.Services;

namespace CRM.DTOs.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class ContactCreateRequest : IValidatableObject
{
    [Required, MaxLength(20), AllowedValues("Buyer", "Seller", "Both")]
    public string Type { get; set; } = string.Empty;
    [Required, MaxLength(80)]
    public string FirstName { get; set; } = string.Empty;
    [Required, MaxLength(80)]
    public string LastName { get; set; } = string.Empty;
    private string? _email;
    [MaxLength(150), EmailAddress]
    public string? Email { get => _email; set => _email = ContactChannels.Email(value); }
    private string? _phone;
    [MaxLength(30), RegularExpression(@"\+[1-9][0-9]{6,14}", ErrorMessage = "Use an international phone number, for example +639171234567.")]
    public string? Phone { get => _phone; set => _phone = ContactChannels.Phone(value); }
    [Required, MaxLength(100), AllowedValues("Walk-in", "Referral", "Website", "Facebook", "Phone", "Other")]
    public string Source { get; set; } = string.Empty;
    [Range(1, int.MaxValue)]
    public virtual int? OwnerId { get; set; }
    public bool IsManuallyInactive { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(Phone))
            yield return new ValidationResult("Enter an email address or phone number.", [nameof(Email), nameof(Phone)]);
    }
}

public class ContactUpdateRequest : ContactCreateRequest
{
    [Required, Range(1, int.MaxValue)]
    public override int? OwnerId { get; set; }
}
