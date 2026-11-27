using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace CRM.Models;
public class Opportunity : AuditableEntity
{
    public int BuyerContactId { get; set; }
    public int PropertyId { get; set; }
    public int AgentId { get; set; }
    public int StageId { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? OfferAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? FinalPrice { get; set; }
    public decimal CommissionRate { get; set; }
    [Required, MaxLength(20), AllowedValues("Open", "Won", "Lost")]
    public string Status { get; set; } = "Open";
    [MaxLength(50)] public string? LostReason { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Contact BuyerContact { get; set; } = null!;
    public Property Property { get; set; } = null!;
    public User Agent { get; set; } = null!;
    public PipelineStage Stage { get; set; } = null!;
    public ICollection<Activity> Activities { get; set; } = new List<Activity>();
}
