using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CRM.DTOs.Requests;

// Stage creation is performed by the migration, not this API contract.
public class PipelineStageCreateRequest
{
    [Required, MaxLength(50)]
    public string Name { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class PipelineStageUpdateRequest
{
    [Required, MaxLength(50)]
    public string Name { get; set; } = string.Empty;
}
