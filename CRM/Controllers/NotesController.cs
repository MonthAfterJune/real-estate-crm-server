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
public class NotesController : ControllerBase
{
    private readonly INoteService _service;
    private readonly IMapper _mapper;

    public NotesController(INoteService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<NoteResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<NoteResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var notes = await _service.GetAllAsync(cancellationToken: cancellationToken);
        return Ok(_mapper.Map<IEnumerable<NoteResponse>>(notes));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(NoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NoteResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var note = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (note == null)
        {
            return NotFound();
        }
        return Ok(_mapper.Map<NoteResponse>(note));
    }

    [HttpPost]
    [ProducesResponseType(typeof(NoteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NoteResponse>> Post([FromBody] NoteCreateRequest request, CancellationToken cancellationToken)
    {
        var note = _mapper.Map<Note>(request);
        var created = await _service.CreateAsync(note, cancellationToken);
        var response = _mapper.Map<NoteResponse>(created);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(NoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NoteResponse>> Put(int id, [FromBody] NoteUpdateRequest request, CancellationToken cancellationToken)
    {
        var note = _mapper.Map<Note>(request);
        var updated = await _service.UpdateAsync(id, note, cancellationToken);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(_mapper.Map<NoteResponse>(updated));
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

