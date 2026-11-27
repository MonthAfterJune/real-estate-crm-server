using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CRM.DTOs.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class OpportunityCreateRequest
{
    [Range(1, int.MaxValue)]
    public int BuyerContactId { get; set; }
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }
    [Range(1, int.MaxValue)]
    public virtual int? AgentId { get; set; }
    [Range(1, int.MaxValue)]
    public virtual int? StageId { get; set; }
    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal? OfferAmount { get; set; }
}

public class OpportunityUpdateRequest : OpportunityCreateRequest
{
    [Required, Range(1, int.MaxValue)]
    public override int? AgentId { get; set; }
    [Required, Range(1, int.MaxValue)]
    public override int? StageId { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class OpportunityWonRequest : IValidatableObject
{
    [Required, Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal? FinalPrice { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FinalPrice is decimal value && value != decimal.Round(value, 2))
            yield return new ValidationResult("Use at most two decimal places.", [nameof(FinalPrice)]);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class OpportunityLostRequest
{
    [Required, AllowedValues("Price", "Bought elsewhere", "Financing", "Changed mind", "Property sold", "No response", "Other")]
    public string LostReason { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class OpportunityCommissionRateRequest : IValidatableObject
{
    [Required, Range(typeof(decimal), "0", "1")]
    public decimal? CommissionRate { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CommissionRate is decimal value && value != decimal.Round(value, 6))
            yield return new ValidationResult("Use at most six decimal places.", [nameof(CommissionRate)]);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class OpportunityReopenRequest : IValidatableObject
{
    [Required]
    public string Reason { get; set; } = string.Empty;
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Reason is not null && Reason.AsSpan().Trim().Length > 2000)
            yield return new ValidationResult("Use a reopening reason of at most 2000 characters.", [nameof(Reason)]);
    }
}
