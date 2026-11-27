namespace CRM.DTOs.Responses;

public class AuditedResponse
{
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public class RoleResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class UserRoleResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class UserResponse : AuditedResponse
{
    public string? Phone { get; set; }
    public bool IsActive { get; set; }
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public UserRoleResponse? Role { get; set; }
}

public class ContactOwnerResponse
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
}

public class ContactResponse : AuditedResponse
{
    public string? Phone { get; set; }
    public bool IsManuallyInactive { get; set; }
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int OwnerId { get; set; }
    public ContactOwnerResponse? Owner { get; set; }
}

public class PipelineStageResponse
{
    public string Code { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class OpportunityStageResponse
{
    public string Code { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class OpportunityBuyerContactResponse
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class OpportunityPropertyResponse
{
    public int Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class OpportunityAgentResponse
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class OpportunityResponse : AuditedResponse
{
    public decimal? CommissionAmount { get; set; }
    public int Id { get; set; }
    public int BuyerContactId { get; set; }
    public OpportunityBuyerContactResponse? BuyerContact { get; set; }
    public int PropertyId { get; set; }
    public OpportunityPropertyResponse? Property { get; set; }
    public int AgentId { get; set; }
    public OpportunityAgentResponse? Agent { get; set; }
    public int StageId { get; set; }
    public OpportunityStageResponse? Stage { get; set; }
    public decimal? OfferAmount { get; set; }
    public decimal? FinalPrice { get; set; }
    public decimal CommissionRate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? LostReason { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public class ActivityResponse : AuditedResponse
{
    public string Subject { get; set; } = string.Empty;
    public string? Details { get; set; }
    public int UserId { get; set; }
    public int CreatedById { get; set; }
    public int? PropertyId { get; set; }
    public int? FromStageId { get; set; }
    public int? ToStageId { get; set; }
    public bool IsSystemGenerated { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public int ContactId { get; set; }
    public int? OpportunityId { get; set; }
}

public class NoteAuthorResponse
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
}

public class NoteResponse : AuditedResponse
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public NoteAuthorResponse? Author { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string Body { get; set; } = string.Empty;
}

public class PropertySellerContactResponse
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public class PropertyListingAgentResponse
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class PropertyResponse : AuditedResponse
{
    public string City { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? Bedrooms { get; set; }
    public int SellerContactId { get; set; }
    public PropertySellerContactResponse? SellerContact { get; set; }
    public int ListingAgentId { get; set; }
    public PropertyListingAgentResponse? ListingAgent { get; set; }
}


