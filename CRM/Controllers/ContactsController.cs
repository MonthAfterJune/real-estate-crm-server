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
public class ContactsController : ControllerBase
{
    private readonly IContactService _service;
    private readonly IMapper _mapper;
    private readonly IUserService _users;

    public ContactsController(IContactService service, IMapper mapper, IUserService users)
    {
        _service = service;
        _mapper = mapper;
        _users = users;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ContactResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ContactResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var contacts = await _service.GetAllAsync(cancellationToken: cancellationToken);
        return Ok(_mapper.Map<IEnumerable<ContactResponse>>(contacts));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ContactResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContactResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var contact = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (contact == null)
        {
            return NotFound();
        }
        return Ok(_mapper.Map<ContactResponse>(contact));
    }

    [HttpPost]
    [ProducesResponseType(typeof(ContactResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ContactResponse>> Post([FromBody] ContactCreateRequest request, CancellationToken cancellationToken)
    {
        request.OwnerId ??= StaffAccess.DefaultAgent(User);
        var owner = request.OwnerId is int ownerId ? await _users.GetByIdAsync(ownerId, cancellationToken: cancellationToken) : null;
        if (owner is null || !owner.IsActive || owner.Role?.Name.ToLowerInvariant() != "agent")
            ModelState.AddModelError(nameof(request.OwnerId), "Choose an active agent as the contact owner.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var contact = _mapper.Map<Contact>(request);
        var created = await _service.CreateAsync(contact, cancellationToken);
        var response = _mapper.Map<ContactResponse>(created);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ContactResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ContactResponse>> Put(int id, [FromBody] ContactUpdateRequest request, CancellationToken cancellationToken)
    {
        var owner = await _users.GetByIdAsync(request.OwnerId!.Value, cancellationToken: cancellationToken);
        if (owner is null || !owner.IsActive || owner.Role?.Name.ToLowerInvariant() != "agent")
            ModelState.AddModelError(nameof(request.OwnerId), "Choose an active agent as the contact owner.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var contact = _mapper.Map<Contact>(request);
        var updated = await _service.UpdateAsync(id, contact, cancellationToken);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(_mapper.Map<ContactResponse>(updated));
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


