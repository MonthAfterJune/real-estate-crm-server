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
public class UsersController : ControllerBase
{
    private readonly IUserService _service;
    private readonly IMapper _mapper;

    public UsersController(IUserService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<UserResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<UserResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var users = await _service.GetAllAsync(cancellationToken: cancellationToken);
        return Ok(_mapper.Map<IEnumerable<UserResponse>>(users));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var user = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (user == null)
        {
            return NotFound();
        }
        return Ok(_mapper.Map<UserResponse>(user));
    }

    [Authorize(Policy = "ManageUsers")]
    [HttpPost]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserResponse>> Post([FromBody] UserCreateRequest request, CancellationToken cancellationToken)
    {
        var user = _mapper.Map<User>(request);
        var created = await _service.CreateAsync(user, cancellationToken);
        var response = _mapper.Map<UserResponse>(created);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    [Authorize(Policy = "ManageUsers")]
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserResponse>> Put(int id, [FromBody] UserUpdateRequest request, CancellationToken cancellationToken)
    {
        var user = _mapper.Map<User>(request);
        var updated = await _service.UpdateAsync(id, user, cancellationToken);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(_mapper.Map<UserResponse>(updated));
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
