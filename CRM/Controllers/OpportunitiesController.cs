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
public class OpportunitiesController : ControllerBase
{
    private readonly IOpportunityService _service;
    private readonly IMapper _mapper;
    private readonly IContactService _contacts;
    private readonly IPropertyService _properties;
    private readonly IUserService _users;
    private readonly IPipelineStageService _stages;

    public OpportunitiesController(IOpportunityService service, IMapper mapper, IContactService contacts, IPropertyService properties, IUserService users, IPipelineStageService stages)
    {
        _service = service;
        _mapper = mapper;
        _contacts = contacts;
        _properties = properties;
        _users = users;
        _stages = stages;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<OpportunityResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<OpportunityResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var opportunities = await _service.GetAllAsync(cancellationToken: cancellationToken);
        return Ok(_mapper.Map<IEnumerable<OpportunityResponse>>(opportunities));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(OpportunityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OpportunityResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var opportunity = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (opportunity == null)
        {
            return NotFound();
        }
        return Ok(_mapper.Map<OpportunityResponse>(opportunity));
    }

    [HttpPost]
    [ProducesResponseType(typeof(OpportunityResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OpportunityResponse>> Post([FromBody] OpportunityCreateRequest request, CancellationToken cancellationToken)
    {
        var buyer = await _contacts.GetByIdAsync(request.BuyerContactId, cancellationToken: cancellationToken);
        request.AgentId ??= buyer?.OwnerId;
        var inquiry = (await _stages.GetAllAsync(cancellationToken: cancellationToken)).SingleOrDefault(s => s.Code == "INQUIRY");
        request.StageId ??= inquiry?.Id;
        if (inquiry is not null && request.StageId != inquiry.Id)
            ModelState.AddModelError(nameof(request.StageId), "New opportunities must start at Inquiry.");
        await ValidateReferences(request.BuyerContactId, request.PropertyId, request.AgentId.GetValueOrDefault(), request.StageId.GetValueOrDefault(), cancellationToken);
        await ValidateOffer(request.StageId.GetValueOrDefault(), request.OfferAmount, cancellationToken);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var opportunity = _mapper.Map<Opportunity>(request);
        var created = await _service.CreateAsync(opportunity, cancellationToken);
        var response = _mapper.Map<OpportunityResponse>(created);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(OpportunityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OpportunityResponse>> Put(int id, [FromBody] OpportunityUpdateRequest request, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken: cancellationToken);
        if (existing is null) return NotFound();
        await ValidateReferences(request.BuyerContactId, request.PropertyId, request.AgentId!.Value, request.StageId!.Value, cancellationToken, existing.BuyerContactId);
        await ValidateOffer(request.StageId.Value, request.OfferAmount, cancellationToken);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var opportunity = _mapper.Map<Opportunity>(request);
        var updated = await _service.UpdateAsync(id, opportunity, cancellationToken);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(_mapper.Map<OpportunityResponse>(updated));
    }

    private async Task ValidateReferences(int buyerContactId, int propertyId, int agentId, int stageId, CancellationToken token, int? retainedBuyerId = null)
    {
        var buyer = await _contacts.GetByIdAsync(buyerContactId, cancellationToken: token);
        if ((buyer is null && retainedBuyerId != buyerContactId) || (buyer is not null && buyer.Type is not ("Buyer" or "Both")))
            ModelState.AddModelError("BuyerContactId", "Choose an existing contact with type Buyer or Both.");
        if (await _properties.GetByIdAsync(propertyId, cancellationToken: token) is null)
            ModelState.AddModelError("PropertyId", "Choose an existing property.");
        var agent = await _users.GetByIdAsync(agentId, cancellationToken: token);
        if (agent is null || !agent.IsActive || agent.Role?.Name.ToLowerInvariant() != "agent")
            ModelState.AddModelError("AgentId", "Choose an existing agent.");
        if (await _stages.GetByIdAsync(stageId, cancellationToken: token) is null)
            ModelState.AddModelError("StageId", "Choose an existing pipeline stage.");
    }

    [HttpPost("{id}/won")]
    public async Task<ActionResult<OpportunityResponse>> Won(int id, OpportunityWonRequest request, CancellationToken token)
    {
        var result = await _service.MarkWonAsync(id, request.FinalPrice!.Value, token);
        return result is null ? NotFound() : Ok(_mapper.Map<OpportunityResponse>(result));
    }

    [HttpPost("{id}/lost")]
    public async Task<ActionResult<OpportunityResponse>> Lost(int id, OpportunityLostRequest request, CancellationToken token)
    {
        var result = await _service.MarkLostAsync(id, request.LostReason, token);
        return result is null ? NotFound() : Ok(_mapper.Map<OpportunityResponse>(result));
    }

    [Authorize(Roles = "manager,admin")]
    [HttpPut("{id}/commission-rate")]
    public async Task<ActionResult<OpportunityResponse>> CommissionRate(int id, OpportunityCommissionRateRequest request, CancellationToken token)
    {
        var result = await _service.SetCommissionRateAsync(id, request.CommissionRate!.Value, token);
        return result is null ? NotFound() : Ok(_mapper.Map<OpportunityResponse>(result));
    }

    [Authorize(Roles = "manager,admin")]
    [HttpPost("{id}/reopen")]
    public async Task<ActionResult<OpportunityResponse>> Reopen(int id, OpportunityReopenRequest request, CancellationToken token)
    {
        var result = await _service.ReopenAsync(id, request.Reason, token);
        return result is null ? NotFound() : Ok(_mapper.Map<OpportunityResponse>(result));
    }

    private async Task ValidateOffer(int stageId, decimal? offer, CancellationToken token)
    {
        var stage = await _stages.GetByIdAsync(stageId, cancellationToken: token);
        if (stage is not null && stage.SortOrder >= 3 && !(offer > 0))
            ModelState.AddModelError(nameof(OpportunityCreateRequest.OfferAmount), "Offer and later stages require a positive offer amount.");
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


