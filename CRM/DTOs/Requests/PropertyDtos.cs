using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CRM.DTOs.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class PropertyCreateRequest : IValidatableObject
{
    [Required, MaxLength(200)]
    public string Address { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string City { get; set; } = string.Empty;
    [Required, MaxLength(50)]
    public string PropertyType { get; set; } = string.Empty;
    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? Price { get; set; }
    [Required, MaxLength(20), AllowedValues("Draft", "Available", "Reserved", "Sold", "Withdrawn")]
    public string Status { get; set; } = "Draft";
    [Range(0, int.MaxValue)]
    public int? Bedrooms { get; set; }
    [Required, Range(1, int.MaxValue)]
    public int? SellerContactId { get; set; }
    [Range(1, int.MaxValue)]
    public virtual int? ListingAgentId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status is "Available" or "Reserved" or "Sold" && !(Price > 0))
            yield return new ValidationResult("This property status requires a price greater than zero.", [nameof(Price)]);
    }
}

public class PropertyUpdateRequest : PropertyCreateRequest
{
    [Required, Range(1, int.MaxValue)]
    public override int? ListingAgentId { get; set; }
}
