using AutoMapper;
using CRM.DTOs.Requests;
using CRM.DTOs.Responses;
using CRM.Services.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Controllers;

[Authorize(Roles = "agent,manager,admin")]
[ApiController]
[Route("api/[controller]")]
public class PipelineStagesController(IPipelineStageService service, IMapper mapper) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<PipelineStageResponse>>> GetAll(CancellationToken token) =>
        Ok(mapper.Map<IEnumerable<PipelineStageResponse>>(await service.GetAllAsync(cancellationToken: token)));

    [HttpGet("{id}")]
    public async Task<ActionResult<PipelineStageResponse>> GetById(int id, CancellationToken token)
    {
        var stage = await service.GetByIdAsync(id, cancellationToken: token);
        return stage is null ? NotFound() : Ok(mapper.Map<PipelineStageResponse>(stage));
    }

    [Authorize(Roles = "admin")]
    [HttpPut("{id}")]
    public async Task<ActionResult<PipelineStageResponse>> Put(int id, PipelineStageUpdateRequest request, CancellationToken token)
    {
        var stage = await service.GetByIdAsync(id, cancellationToken: token);
        if (stage is null) return NotFound();
        stage.Name = request.Name.Trim();
        var updated = await service.UpdateAsync(id, stage, token);
        return updated is null ? NotFound() : Ok(mapper.Map<PipelineStageResponse>(updated));
    }
}
