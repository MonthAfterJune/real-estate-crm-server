using Microsoft.AspNetCore.Authorization;
using AutoMapper;
using CRM.DTOs.Requests;
using CRM.DTOs.Responses;
using CRM.Models;
using CRM.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using CRM.Services;

namespace CRM.Controllers;

[Authorize(Roles = "agent,manager,admin")]
[ApiController]
[Route("api/[controller]")]
public class PropertiesController : ControllerBase
{
    private readonly IPropertyService _service;
    private readonly IMapper _mapper;
    private readonly IContactService _contacts;
    private readonly IUserService _users;

    public PropertiesController(IPropertyService service, IMapper mapper, IContactService contacts, IUserService users)
    {
        _service = service;
        _mapper = mapper;
        _contacts = contacts;
        _users = users;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PropertyResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PropertyResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var properties = await _service.GetAllAsync(cancellationToken: cancellationToken);
        return Ok(_mapper.Map<IEnumerable<PropertyResponse>>(properties));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PropertyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var property = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (property == null)
        {
            return NotFound();
        }
        return Ok(_mapper.Map<PropertyResponse>(property));
    }

    [HttpPost]
    [ProducesResponseType(typeof(PropertyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PropertyResponse>> Post([FromBody] PropertyCreateRequest request, CancellationToken cancellationToken)
    {
        request.ListingAgentId ??= StaffAccess.DefaultAgent(User);
        await ValidateAssignments(request.SellerContactId!.Value, request.ListingAgentId.GetValueOrDefault(), cancellationToken);
        if (request.Status != "Draft") ModelState.AddModelError(nameof(request.Status), "New properties must start as Draft.");
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var property = _mapper.Map<Property>(request);
        var created = await _service.CreateAsync(property, cancellationToken);
        var response = _mapper.Map<PropertyResponse>(created);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(PropertyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PropertyResponse>> Put(int id, [FromBody] PropertyUpdateRequest request, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (existing is null) return NotFound();
        await ValidateAssignments(request.SellerContactId!.Value, request.ListingAgentId!.Value, cancellationToken, existing.SellerContactId);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var property = _mapper.Map<Property>(request);
        var updated = await _service.UpdateAsync(id, property, cancellationToken);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(_mapper.Map<PropertyResponse>(updated));
    }

    private async Task ValidateAssignments(int sellerContactId, int listingAgentId, CancellationToken cancellationToken, int? retainedSellerId = null)
    {
        var seller = await _contacts.GetByIdAsync(sellerContactId, cancellationToken: cancellationToken);
        if ((seller == null && retainedSellerId != sellerContactId) || (seller is not null && seller.Type is not ("Seller" or "Both")))
        {
            ModelState.AddModelError(nameof(PropertyCreateRequest.SellerContactId),
                "Choose an existing contact with type Seller or Both.");
        }
        var agent = await _users.GetByIdAsync(listingAgentId, cancellationToken: cancellationToken);
        if (agent is null || !agent.IsActive || agent.Role?.Name.ToLowerInvariant() != "agent")
        {
            ModelState.AddModelError(nameof(PropertyCreateRequest.ListingAgentId),
                "Choose an existing agent as the listing agent.");
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var deleted = await _service.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound();
        }
        return NoContent();
    }
}




