using System.ComponentModel.DataAnnotations;
namespace CRM.Models;
public class Contact : AuditableEntity
{
    [Required, MaxLength(20), AllowedValues("Buyer", "Seller", "Both")]
    public string Type { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string FirstName { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string LastName { get; set; } = string.Empty;
    [MaxLength(150)] public string? Email { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    [Required, MaxLength(50), AllowedValues("Lead", "Active", "Client", "Inactive")]
    public string Status { get; set; } = "Lead";
    [Required, MaxLength(100), AllowedValues("Walk-in", "Referral", "Website", "Facebook", "Phone", "Other")]
    public string Source { get; set; } = "Other";
    public bool IsManuallyInactive { get; set; }
    [Range(1, int.MaxValue)] public int OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public ICollection<Property> ListedAsSeller { get; set; } = new List<Property>();
    public ICollection<Opportunity> Purchases { get; set; } = new List<Opportunity>();
    public ICollection<Activity> Activities { get; set; } = new List<Activity>();
}
