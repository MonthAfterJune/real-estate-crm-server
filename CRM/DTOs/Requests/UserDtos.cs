using System.ComponentModel.DataAnnotations;

namespace CRM.DTOs.Requests;

public class UserCreateRequest
{
    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public int RoleId { get; set; }
    [MaxLength(30)]
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UserUpdateRequest
{
    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public int RoleId { get; set; }
    [MaxLength(30)]
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}
