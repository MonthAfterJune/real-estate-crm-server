using System.ComponentModel.DataAnnotations;
namespace CRM.Models;
public class Document : AuditableEntity
{
    public int UploadedById { get; set; }
    [Required, MaxLength(50), AllowedValues("Contact", "Property", "Opportunity")]
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    [Required, MaxLength(30), AllowedValues("ID", "Offer", "Contract", "Title", "Photo", "Other")]
    public string Category { get; set; } = string.Empty;
    [Required, MaxLength(255)] public string FileName { get; set; } = string.Empty;
    [Required, MaxLength(2048)] public string FilePath { get; set; } = string.Empty;
    public User UploadedBy { get; set; } = null!;
}
