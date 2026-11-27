namespace CRM.DTOs.Responses;

public sealed record AuthUserResponse(int Id, string FullName, string Email, string Role);
