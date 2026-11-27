using System.ComponentModel.DataAnnotations;

namespace CRM.DTOs.Requests;

public class NoteCreateRequest
{
    [Required]
    public int AuthorId { get; set; }

    [Required]
    [MaxLength(50)]
    [AllowedValues("Contact", "Property", "Opportunity")]
    public string EntityType { get; set; } = string.Empty;

    [Required, Range(1, int.MaxValue)]
    public int EntityId { get; set; }

    [Required]
    public string Body { get; set; } = string.Empty;
}

public class NoteUpdateRequest
{
    [Required]
    public int AuthorId { get; set; }

    [Required]
    [MaxLength(50)]
    [AllowedValues("Contact", "Property", "Opportunity")]
    public string EntityType { get; set; } = string.Empty;

    [Required, Range(1, int.MaxValue)]
    public int EntityId { get; set; }

    [Required]
    public string Body { get; set; } = string.Empty;
}
