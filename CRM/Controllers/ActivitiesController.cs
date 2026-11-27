using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using AutoMapper;
using CRM.DTOs.Requests;
using CRM.DTOs.Responses;
using CRM.Models;
using CRM.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Controllers;

[Authorize(Roles = "agent,manager,admin")]
[ApiController]
[Route("api/[controller]")]
public class ActivitiesController(IActivityService service, IMapper mapper, IContactService contacts,
    IOpportunityService opportunities, IPropertyService properties, IUserService users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ActivityResponse>>> GetAll(CancellationToken token) =>
        Ok(mapper.Map<IEnumerable<ActivityResponse>>(await service.GetAllAsync(cancellationToken: token)));

    [HttpGet("{id}")]
    public async Task<ActionResult<ActivityResponse>> GetById(int id, CancellationToken token)
    {
        var entity = await service.GetByIdAsync(id, cancellationToken: token);
        return entity is null ? NotFound() : Ok(mapper.Map<ActivityResponse>(entity));
    }

    [HttpPost]
    public async Task<ActionResult<ActivityResponse>> Post(ActivityCreateRequest request, CancellationToken token)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var author)) return Unauthorized();
        await Validate(request, author, token, request.CompletedAt is not null, true);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var entity = mapper.Map<Activity>(request);
        entity.CreatedById = author;
        var created = await service.CreateAsync(entity, token);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, mapper.Map<ActivityResponse>(created));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ActivityResponse>> Put(int id, ActivityUpdateRequest request, CancellationToken token)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var author)) return Unauthorized();
        var existing = await service.GetByIdAsync(id, cancellationToken: token);
        if (existing is null) return NotFound();
        if (existing.IsSystemGenerated) return Forbid();
        var completing = existing.CompletedAt is null && request.CompletedAt is not null;
        await Validate(request, author, token, completing, existing.UserId != request.UserId);
        // The assignee may complete a task created by someone else, but may not
        // use that exception to edit its author-controlled metadata.
        if (existing.CreatedById != author && !IsManager()
            && !(completing && existing.UserId == author && SameMetadata(existing, request))) return Forbid();
        if (existing.CompletedAt is not null && existing.UserId != request.UserId)
            ModelState.AddModelError(nameof(request.UserId), "The performer of a completed activity cannot change.");
        if (existing.CompletedAt is not null && request.CompletedAt is null)
            ModelState.AddModelError(nameof(request.CompletedAt), "A completed activity cannot be reset through this update.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var updated = await service.UpdateAsync(id, mapper.Map<Activity>(request), token);
        return updated is null ? NotFound() : Ok(mapper.Map<ActivityResponse>(updated));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken token)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var author)) return Unauthorized();
        var existing = await service.GetByIdAsync(id, cancellationToken: token);
        if (existing is null) return NotFound();
        if (existing.IsSystemGenerated || (existing.CreatedById != author && !IsManager())) return Forbid();
        return await service.DeleteAsync(id, token) ? NoContent() : NotFound();
    }

    private bool IsManager() => User.IsInRole("manager") || User.IsInRole("admin");

    private static bool SameMetadata(Activity existing, ActivityCreateRequest request) =>
        existing.Type == request.Type && existing.Subject == request.Subject && existing.Details == request.Details
        && existing.ContactId == request.ContactId && existing.OpportunityId == request.OpportunityId
        && existing.PropertyId == request.PropertyId && existing.UserId == request.UserId && existing.ScheduledAt == request.ScheduledAt;

    private async Task Validate(ActivityCreateRequest request, int author, CancellationToken token, bool completing, bool requireActiveAssignee)
    {
        if (request.OpportunityId is int opportunityId)
        {
            var opportunity = await opportunities.GetByIdAsync(opportunityId, cancellationToken: token);
            if (opportunity is null) ModelState.AddModelError(nameof(request.OpportunityId), "Choose an existing opportunity.");
            else
            {
                if (request.ContactId != opportunity.BuyerContactId)
                    ModelState.AddModelError(nameof(request.ContactId), "The contact must be the opportunity buyer.");
                if (request.PropertyId is not null && request.PropertyId != opportunity.PropertyId)
                    ModelState.AddModelError(nameof(request.PropertyId), "The property must match the opportunity.");
                request.PropertyId = opportunity.PropertyId;
            }
        }
        else
        {
            if (await contacts.GetByIdAsync(request.ContactId, cancellationToken: token) is null)
                ModelState.AddModelError(nameof(request.ContactId), "Choose an existing contact.");
            if (request.PropertyId is int propertyId)
            {
                var property = await properties.GetByIdAsync(propertyId, cancellationToken: token);
                if (property is null) ModelState.AddModelError(nameof(request.PropertyId), "Choose an existing property.");
                else if (property.SellerContactId != request.ContactId)
                    ModelState.AddModelError(nameof(request.ContactId), "A property-only activity uses its seller contact.");
            }
        }
        var responsible = await users.GetByIdAsync(request.UserId, cancellationToken: token);
        if (responsible is null || (requireActiveAssignee && (!responsible.IsActive || responsible.Role?.Name.ToLowerInvariant() != "agent")))
            ModelState.AddModelError(nameof(request.UserId), "Choose an active agent.");
        if (completing && request.UserId != author)
            ModelState.AddModelError(nameof(request.UserId), "Only the assigned agent may complete an activity.");
    }
}
