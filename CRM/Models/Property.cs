using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace CRM.Models;
public class Property : AuditableEntity
{
    [Required, MaxLength(200)] public string Address { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string City { get; set; } = string.Empty;
    [Required, MaxLength(50)] public string PropertyType { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")] public decimal? Price { get; set; }
    [Required, MaxLength(20), AllowedValues("Draft", "Available", "Reserved", "Sold", "Withdrawn")]
    public string Status { get; set; } = "Draft";
    [Range(0, int.MaxValue)] public int? Bedrooms { get; set; }
    public int SellerContactId { get; set; }
    public int ListingAgentId { get; set; }
    public Contact SellerContact { get; set; } = null!;
    public User ListingAgent { get; set; } = null!;
    public ICollection<Opportunity> Opportunities { get; set; } = new List<Opportunity>();
    public ICollection<Activity> Activities { get; set; } = new List<Activity>();
}
