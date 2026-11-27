using System.ComponentModel.DataAnnotations;

namespace CRM.DTOs.Requests;

public sealed class RegisterRequest : IValidatableObject
{
    [Required, MaxLength(100), RegularExpression(@".*\S.*", ErrorMessage = "Enter your full name.")]
    public string FullName { get; set; } = string.Empty;
    [Required, EmailAddress, MaxLength(150)]
    public string Email { get; set; } = string.Empty;
    [Required, MinLength(12), MaxLength(128)]
    public string Password { get; set; } = string.Empty;
    [Required, RegularExpression("^(agent|buyer|seller)$", ErrorMessage = "Choose agent, buyer, or seller.")]
    public string Role { get; set; } = string.Empty;
    public bool AcceptTerms { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!AcceptTerms) yield return new ValidationResult("Confirm the account agreement.", [nameof(AcceptTerms)]);
    }
}

public sealed class LoginRequest
{
    [Required, EmailAddress, MaxLength(150)]
    public string Email { get; set; } = string.Empty;
    [Required, MaxLength(128)]
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}
