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
public class RolesController : ControllerBase
{
    private readonly IRoleService _service;
    private readonly IMapper _mapper;

    public RolesController(IRoleService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<RoleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RoleResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var roles = await _service.GetAllAsync(cancellationToken: cancellationToken);
        return Ok(_mapper.Map<IEnumerable<RoleResponse>>(roles));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var role = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (role == null)
        {
            return NotFound();
        }
        return Ok(_mapper.Map<RoleResponse>(role));
    }

    [Authorize(Policy = "ManageUsers")]
    [HttpPost]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RoleResponse>> Post([FromBody] RoleCreateRequest request, CancellationToken cancellationToken)
    {
        var role = _mapper.Map<Role>(request);
        var created = await _service.CreateAsync(role, cancellationToken);
        var response = _mapper.Map<RoleResponse>(created);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    [Authorize(Policy = "ManageUsers")]
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RoleResponse>> Put(int id, [FromBody] RoleUpdateRequest request, CancellationToken cancellationToken)
    {
        var role = _mapper.Map<Role>(request);
        var updated = await _service.UpdateAsync(id, role, cancellationToken);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(_mapper.Map<RoleResponse>(updated));
    }

    [Authorize(Policy = "ManageUsers")]
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
