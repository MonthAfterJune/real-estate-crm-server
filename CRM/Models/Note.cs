using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace CRM.Models;
public class Note : AuditableEntity
{
    public int AuthorId { get; set; }
    [Required, MaxLength(50), AllowedValues("Contact", "Property", "Opportunity")]
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    [Required, Column(TypeName = "TEXT")] public string Body { get; set; } = string.Empty;
    public User Author { get; set; } = null!;
}
