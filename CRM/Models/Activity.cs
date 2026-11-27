using System.ComponentModel.DataAnnotations;
namespace CRM.Models;
public class Activity : AuditableEntity
{
    [Required, MaxLength(50)] public string Type { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string Subject { get; set; } = string.Empty;
    public string? Details { get; set; }
    public int ContactId { get; set; }
    public int? OpportunityId { get; set; }
    public int? PropertyId { get; set; }
    public int UserId { get; set; }
    public int CreatedById { get; set; }
    public int? FromStageId { get; set; }
    public int? ToStageId { get; set; }
    public bool IsSystemGenerated { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Contact Contact { get; set; } = null!;
    public Opportunity? Opportunity { get; set; }
    public Property? Property { get; set; }
    public User User { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
    public PipelineStage? FromStage { get; set; }
    public PipelineStage? ToStage { get; set; }
}
