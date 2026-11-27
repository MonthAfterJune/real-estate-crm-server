using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace CRM.Models;

public class User : IdentityUser<int>, IAuditableEntity
{
    // Phone is an Identity-backed alias; PhoneNumber maps to the database Phone column.
    [NotMapped] public string? Phone { get => PhoneNumber; set => PhoneNumber = value; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }

    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [ForeignKey(nameof(Role))]
    public int RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public ICollection<Opportunity> OwnedOpportunities { get; set; } = new List<Opportunity>();

    public ICollection<Property> ListedProperties { get; set; } = new List<Property>();

    public ICollection<Contact> OwnedContacts { get; set; } = new List<Contact>();

    public ICollection<Activity> Activities { get; set; } = new List<Activity>();
    public ICollection<Activity> CreatedActivities { get; set; } = new List<Activity>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<Note> Notes { get; set; } = new List<Note>();
}
