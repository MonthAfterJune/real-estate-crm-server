using System.ComponentModel.DataAnnotations;

namespace CRM.DTOs.Requests;

public class RoleCreateRequest
{
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;
}

public class RoleUpdateRequest
{
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;
}
