using CRM.Data;
using CRM.Models;
using CRM.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Linq.Expressions;
using System.Text.Json;

namespace CRM.Services;

public class RoleService : CrudService<Role>, IRoleService
{
    public RoleService(ApplicationDbContext context) : base(context)
    {
    }

    public override async Task<IEnumerable<Role>> GetAllAsync(
        IEnumerable<Expression<Func<Role, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= new List<Expression<Func<Role, object>>>();
        return await base.GetAllAsync(includes, cancellationToken);
    }

    public override async Task<Role?> GetByIdAsync(
        int id,
        IEnumerable<Expression<Func<Role, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= new List<Expression<Func<Role, object>>>();
        return await base.GetByIdAsync(id, includes, cancellationToken);
    }
}

public class UserService : CrudService<User>, IUserService
{
    private static readonly List<Expression<Func<User, object>>> _defaultIncludes = new()
    {
        u => u.Role
    };

    private readonly UserManager<User> _users;

    public UserService(ApplicationDbContext context, UserManager<User> users) : base(context)
    {
        _users = users;
    }

    // Directory administration must not overwrite password hashes or Identity state.
    public override async Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default)
    {
        entity.FullName = entity.FullName.Trim();
        entity.Email = entity.Email!.Trim();
        entity.UserName = entity.Email;
        var result = await _users.CreateAsync(entity);
        EnsureSuccess(result);
        return await GetByIdAsync(entity.Id, cancellationToken: cancellationToken) ?? entity;
    }

    public override async Task<User?> UpdateAsync(int id, User entity, CancellationToken cancellationToken = default)
    {
        var existing = await _dbSet.FindAsync(new object?[] { id }, cancellationToken);
        if (existing is null) return null;
        var email = entity.Email!.Trim();
        if (existing.Email != email || existing.RoleId != entity.RoleId || existing.IsActive != entity.IsActive)
        {
            // Existing cookies are rejected when Identity next checks the stamp.
            existing.SecurityStamp = Guid.NewGuid().ToString();
        }
        existing.FullName = entity.FullName.Trim();
        existing.Email = email;
        existing.UserName = email;
        existing.RoleId = entity.RoleId;
        existing.Phone = entity.Phone;
        existing.IsActive = entity.IsActive;
        EnsureSuccess(await _users.UpdateAsync(existing));
        return await GetByIdAsync(id, cancellationToken: cancellationToken) ?? existing;
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new BadHttpRequestException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    public override async Task<IEnumerable<User>> GetAllAsync(
        IEnumerable<Expression<Func<User, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        return await base.GetAllAsync(includes, cancellationToken);
    }

    public override async Task<User?> GetByIdAsync(
        int id,
        IEnumerable<Expression<Func<User, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        return await base.GetByIdAsync(id, includes, cancellationToken);
    }
}

public class ContactService : CrudService<Contact>, IContactService
{
    private readonly StaffAccess _access;
    private readonly Part01Workflow _workflow;
    public ContactService(ApplicationDbContext context, StaffAccess access, Part01Workflow workflow) : base(context)
    { _access = access; _workflow = workflow; }

    public override async Task<Contact> CreateAsync(Contact entity, CancellationToken cancellationToken = default)
    {
        if (entity.OwnerId == 0 && !_access.IsManager) entity.OwnerId = _access.ActorId;
        _access.Assignment(entity.OwnerId, _access.ActorId, nameof(Contact.OwnerId));
        await _access.ActiveAgent(entity.OwnerId, nameof(Contact.OwnerId), cancellationToken);
        await NormalizeAndCheckDuplicate(entity, 0, cancellationToken);
        entity.Status = entity.IsManuallyInactive ? "Inactive" : "Lead";
        return await base.CreateAsync(entity, cancellationToken);
    }

    public override async Task<Contact?> UpdateAsync(int id, Contact entity, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (existing is null || !_access.Owns(existing.OwnerId)) return null;
        if (existing.OwnerId != entity.OwnerId && !_access.IsManager)
            throw new WorkflowException(403, "Only Manager/Admin may reassign a contact.", nameof(Contact.OwnerId));
        await _access.ActiveAgent(entity.OwnerId, nameof(Contact.OwnerId), cancellationToken);
        if (entity.Type == "Buyer" && await _context.Properties.IgnoreQueryFilters().AnyAsync(p => p.SellerContactId == id, cancellationToken)
            || entity.Type == "Seller" && await _context.Opportunities.IgnoreQueryFilters().AnyAsync(o => o.BuyerContactId == id, cancellationToken))
            throw WorkflowException.Invalid(nameof(Contact.Type), "This person has retained buyer/seller references. Choose Both.");
        await NormalizeAndCheckDuplicate(entity, id, cancellationToken);
        entity.Status = entity.IsManuallyInactive ? "Inactive"
            : await _context.Opportunities.AnyAsync(o => o.BuyerContactId == id && o.Status == "Won", cancellationToken)
                || await _context.Properties.AnyAsync(p => p.SellerContactId == id && p.Status == "Sold", cancellationToken) ? "Client"
            : await _context.Opportunities.AnyAsync(o => o.BuyerContactId == id && o.Status == "Open", cancellationToken)
                || await _context.Properties.AnyAsync(p => p.SellerContactId == id && (p.Status == "Available" || p.Status == "Reserved"), cancellationToken) ? "Active" : "Lead";
        return await _workflow.Atomic(async () =>
        {
            await base.UpdateAsync(id, entity, cancellationToken);
            await _workflow.RefreshContacts([id], cancellationToken);
            if (existing.OwnerId != entity.OwnerId)
                await _workflow.History("Contact owner reassigned", id, entity.OwnerId,
                    new { previousOwnerId = existing.OwnerId, ownerId = entity.OwnerId }, cancellationToken);
            return await GetByIdAsync(id, cancellationToken: cancellationToken);
        }, cancellationToken);
    }

    private async Task NormalizeAndCheckDuplicate(Contact entity, int id, CancellationToken token)
    {
        entity.FirstName = entity.FirstName.Trim();
        entity.LastName = entity.LastName.Trim();
        entity.Email = ContactChannels.Email(entity.Email);
        entity.Phone = ContactChannels.Phone(entity.Phone);
        var duplicate = await _context.Contacts.AsNoTracking().Where(c => c.Id != id &&
            ((entity.Email != null && c.Email == entity.Email) || (entity.Phone != null && c.Phone == entity.Phone)))
            .OrderBy(c => c.Id).FirstOrDefaultAsync(token);
        if (duplicate is not null)
        {
            var visible = _access.Owns(duplicate.OwnerId);
            throw new WorkflowException(400, visible ? "A matching contact already exists. Open and reuse that contact."
                : "A matching contact exists. Ask a Manager to resolve the duplicate.",
                entity.Email is not null && entity.Email == duplicate.Email ? nameof(Contact.Email) : nameof(Contact.Phone),
                visible ? duplicate.Id : null);
        }
    }

    public override async Task<IEnumerable<Contact>> GetAllAsync(
        IEnumerable<Expression<Func<Contact, object>>>? includes = null,
        CancellationToken cancellationToken = default) => await Query(includes).ToListAsync(cancellationToken);

    public override async Task<Contact?> GetByIdAsync(int id,
        IEnumerable<Expression<Func<Contact, object>>>? includes = null,
        CancellationToken cancellationToken = default) =>
        await Query(includes).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
    private IQueryable<Contact> Query(IEnumerable<Expression<Func<Contact, object>>>? includes)
    {
        var actor = _access.ActorId; var manager = _access.IsManager;
        IQueryable<Contact> query = _dbSet.AsNoTracking().Where(c => manager || c.OwnerId == actor);
        foreach (var include in includes ?? [c => c.Owner]) query = query.Include(include);
        return query;
    }
    public override async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        await GetByIdAsync(id, cancellationToken: cancellationToken) is not null && await base.DeleteAsync(id, cancellationToken);
}

public class PipelineStageService : CrudService<PipelineStage>, IPipelineStageService
{
    public PipelineStageService(ApplicationDbContext context) : base(context)
    {
    }
}

public class OpportunityService : CrudService<Opportunity>, IOpportunityService
{
    private static readonly List<Expression<Func<Opportunity, object>>> _defaultIncludes = new()
    {
        o => o.BuyerContact,
        o => o.Property,
        o => o.Agent,
        o => o.Stage
    };

    private readonly IConfiguration _configuration;
    private readonly StaffAccess _access;
    private readonly Part01Workflow _workflow;

    public OpportunityService(ApplicationDbContext context, IConfiguration configuration, StaffAccess access, Part01Workflow workflow) : base(context)
    {
        _configuration = configuration;
        _access = access;
        _workflow = workflow;
    }

    public override async Task<Opportunity> CreateAsync(Opportunity entity, CancellationToken cancellationToken = default)
    {
        var buyer = await _context.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == entity.BuyerContactId, cancellationToken);
        if (buyer is null || !_access.Owns(buyer.OwnerId) || buyer.Type is not ("Buyer" or "Both"))
            throw WorkflowException.Invalid(nameof(Opportunity.BuyerContactId), "Choose a visible Buyer or Both contact.");
        if (entity.AgentId == 0) entity.AgentId = buyer.OwnerId;
        _access.Assignment(entity.AgentId, buyer.OwnerId, nameof(Opportunity.AgentId));
        await _access.ActiveAgent(entity.AgentId, nameof(Opportunity.AgentId), cancellationToken);
        var inquiry = await _context.PipelineStages.SingleOrDefaultAsync(s => s.Code == "INQUIRY", cancellationToken)
            ?? throw new WorkflowException(503, "The Inquiry stage is unavailable.");
        if (entity.StageId != 0 && entity.StageId != inquiry.Id)
            throw WorkflowException.Invalid(nameof(Opportunity.StageId), "New opportunities must start at Inquiry.");
        entity.StageId = inquiry.Id;
        if (!decimal.TryParse(_configuration["DefaultCommissionRate"], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate < 0 || rate > 1)
            throw new WorkflowException(503, "DefaultCommissionRate must be configured as a decimal fraction between zero and one.");
        entity.CommissionRate = rate;
        entity.Status = "Open";
        entity.FinalPrice = null;
        entity.LostReason = null;
        entity.ClosedAt = null;
        return await _workflow.Atomic(async () =>
        {
            await _workflow.LockProperty(entity.PropertyId, cancellationToken);
            var property = await _context.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == entity.PropertyId, cancellationToken);
            if (property is null || property.Status is not ("Available" or "Reserved"))
                throw WorkflowException.Invalid(nameof(Opportunity.PropertyId), "Choose an Available or Reserved property for an inquiry.");
            if (await _context.Opportunities.AnyAsync(o => o.BuyerContactId == entity.BuyerContactId && o.PropertyId == entity.PropertyId && o.Status == "Open", cancellationToken))
                throw new WorkflowException(409, "An Open opportunity already exists for this buyer and property.", nameof(Opportunity.BuyerContactId));
            if (property.Status != "Available" && await _context.Opportunities.IgnoreQueryFilters().AnyAsync(o => o.BuyerContactId == entity.BuyerContactId && o.PropertyId == entity.PropertyId && o.Status != "Open", cancellationToken))
                throw WorkflowException.Invalid(nameof(Opportunity.PropertyId), "A new attempt after a closed opportunity requires an Available property.");
            await _workflow.LockContacts([entity.BuyerContactId], cancellationToken);
            var created = await base.CreateAsync(entity, cancellationToken);
            await _workflow.RefreshContacts([entity.BuyerContactId], cancellationToken);
            return await GetByIdAsync(created.Id, cancellationToken: cancellationToken) ?? created;
        }, cancellationToken);
    }

    public override async Task<Opportunity?> UpdateAsync(int id, Opportunity entity, CancellationToken cancellationToken = default)
    {
        var preliminary = await _context.Opportunities.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (preliminary is null || !_access.Owns(preliminary.AgentId)) return null;
        return await _workflow.Atomic(async () =>
        {
            foreach (var propertyId in new[] { preliminary.PropertyId, entity.PropertyId }.Distinct().Order())
                await _workflow.LockProperty(propertyId, cancellationToken);
            if (await _workflow.LockOpportunity(id, cancellationToken) == 0) return null;
            var existing = await _context.Opportunities.AsNoTracking().Include(o => o.Stage).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
            if (existing is null || !_access.Owns(existing.AgentId)) return null;
            if (existing.PropertyId != preliminary.PropertyId) throw AssociationConflict();
            if (existing.Status != "Open") throw WorkflowException.Invalid(nameof(Opportunity.StageId), "Closed opportunities cannot be changed.");
            var property = await _context.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == entity.PropertyId, cancellationToken)
                ?? throw WorkflowException.Invalid(nameof(Opportunity.PropertyId), "Choose an existing property.");
            var destination = await _context.PipelineStages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == entity.StageId, cancellationToken)
                ?? throw WorkflowException.Invalid(nameof(Opportunity.StageId), "Choose an existing pipeline stage.");
            if (entity.AgentId != existing.AgentId && !_access.IsManager)
                throw new WorkflowException(403, "Only Manager/Admin may reassign an opportunity.", nameof(Opportunity.AgentId));
            await _access.ActiveAgent(entity.AgentId, nameof(Opportunity.AgentId), cancellationToken);
            var buyer = await _context.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == entity.BuyerContactId, cancellationToken);
            if (buyer is null || buyer.Type is not ("Buyer" or "Both")
                || (entity.BuyerContactId != existing.BuyerContactId && !_access.Owns(buyer.OwnerId)))
                throw WorkflowException.Invalid(nameof(Opportunity.BuyerContactId), "Choose a visible Buyer or Both contact, or retain the existing buyer reference.");
            var changingStage = existing.StageId != destination.Id;
            var wasExclusive = Exclusive(existing.Stage.Code);
            var becomesExclusive = Exclusive(destination.Code);
            if (destination.SortOrder < existing.Stage.SortOrder - 1)
                throw WorkflowException.Invalid(nameof(Opportunity.StageId), "Move backward only one stage at a time.");
            if (destination.SortOrder >= 3 && !(entity.OfferAmount > 0))
                throw WorkflowException.Invalid(nameof(Opportunity.OfferAmount), "Offer and later stages require a positive offer amount.");
            if (entity.PropertyId != existing.PropertyId && (wasExclusive || becomesExclusive))
                throw WorkflowException.Invalid(nameof(Opportunity.PropertyId), "Leave Reservation/Closing before changing the property in a separate save.");
            if (changingStage && property.Status is not ("Available" or "Reserved"))
                throw WorkflowException.Invalid(nameof(Opportunity.StageId), "Stage progression requires an Available or Reserved property.");
            if (entity.PropertyId != existing.PropertyId || entity.BuyerContactId != existing.BuyerContactId)
            {
                if (property.Status is not ("Available" or "Reserved"))
                    throw WorkflowException.Invalid(nameof(Opportunity.PropertyId), "Choose an Available or Reserved property.");
                if (property.Status != "Available" && await _context.Opportunities.IgnoreQueryFilters().AnyAsync(o => o.Id != id && o.BuyerContactId == entity.BuyerContactId && o.PropertyId == entity.PropertyId && o.Status != "Open", cancellationToken))
                    throw WorkflowException.Invalid(nameof(Opportunity.PropertyId), "A new attempt after a closed opportunity requires an Available property.");
            }
            if (await _context.Opportunities.AnyAsync(o => o.Id != id && o.BuyerContactId == entity.BuyerContactId && o.PropertyId == entity.PropertyId && o.Status == "Open", cancellationToken))
                throw new WorkflowException(409, "An Open opportunity already exists for this buyer and property.", nameof(Opportunity.BuyerContactId));
            var otherHolder = await _context.Opportunities.AnyAsync(o => o.Id != id && o.PropertyId == property.Id && o.Status == "Open"
                && (o.Stage.Code == "RESERVATION" || o.Stage.Code == "CLOSING"), cancellationToken);
            var oldPropertyStatus = property.Status;
            if (wasExclusive)
            {
                if (property.Status != "Reserved" || otherHolder) throw ReservationConflict();
                if (!becomesExclusive) property.Status = "Available";
            }
            else if (becomesExclusive)
            {
                if (property.Status != "Available" || otherHolder) throw ReservationConflict();
                property.Status = "Reserved";
            }
            await _workflow.LockContacts([existing.BuyerContactId, entity.BuyerContactId, property.SellerContactId], cancellationToken);
            _workflow.Detach<Opportunity>(id);
            await base.UpdateAsync(id, entity, cancellationToken);
            if (oldPropertyStatus != property.Status)
                await _context.Properties.Where(p => p.Id == property.Id).ExecuteUpdateAsync(set => set
                    .SetProperty(p => p.Status, property.Status).SetProperty(p => p.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (changingStage)
            {
                _context.Activities.Add(new Activity
                {
                    Type = "Stage Change", Subject = $"{existing.Stage.Name} → {destination.Name}",
                    ContactId = entity.BuyerContactId, OpportunityId = id, PropertyId = entity.PropertyId,
                    FromStageId = existing.StageId, ToStageId = destination.Id, UserId = entity.AgentId, CreatedById = _access.ActorId,
                    IsSystemGenerated = true, CompletedAt = DateTime.UtcNow,
                    Details = JsonSerializer.Serialize(new { reason = "Opportunity stage changed", previousStageCode = existing.Stage.Code, stageCode = destination.Code,
                        previousStatus = existing.Status, status = existing.Status, previousOfferAmount = existing.OfferAmount, offerAmount = entity.OfferAmount,
                        previousPropertyStatus = oldPropertyStatus, propertyStatus = property.Status })
                });
                await _context.SaveChangesAsync(cancellationToken);
            }
            if (existing.AgentId != entity.AgentId)
                await _workflow.History("Opportunity Agent reassigned", entity.BuyerContactId, entity.AgentId,
                    new { previousAgentId = existing.AgentId, agentId = entity.AgentId }, cancellationToken, entity.PropertyId, id);
            await _workflow.RefreshContacts([existing.BuyerContactId, entity.BuyerContactId, property.SellerContactId], cancellationToken);
            return await GetByIdAsync(id, cancellationToken: cancellationToken);
        }, cancellationToken);
    }

    private static bool Exclusive(string code) => code is "RESERVATION" or "CLOSING";
    private static WorkflowException ReservationConflict() => new(409, "The property's reservation state has changed or is unavailable. Reload before trying again.", nameof(Opportunity.StageId));
    private static WorkflowException AssociationConflict() => new(409, "The opportunity changed while saving. Reload before trying again.");
    private static readonly string[] LostReasons = ["Price", "Bought elsewhere", "Financing", "Changed mind", "Property sold", "No response", "Other"];

    public Task<Opportunity?> MarkWonAsync(int id, decimal finalPrice, CancellationToken cancellationToken = default)
    {
        if (finalPrice <= 0 || finalPrice > 9999999999999999.99m || finalPrice != decimal.Round(finalPrice, 2))
            throw WorkflowException.Invalid(nameof(Opportunity.FinalPrice), "Use a positive final price with at most two decimal places.");
        return Close(id, finalPrice, null, cancellationToken);
    }

    public Task<Opportunity?> MarkLostAsync(int id, string lostReason, CancellationToken cancellationToken = default)
    {
        if (!LostReasons.Contains(lostReason))
            throw WorkflowException.Invalid(nameof(Opportunity.LostReason), "Choose a supported lost reason.");
        return Close(id, null, lostReason, cancellationToken);
    }

    private async Task<Opportunity?> Close(int id, decimal? finalPrice, string? lostReason, CancellationToken token)
    {
        var preliminary = await _context.Opportunities.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, token);
        if (preliminary is null || !_access.Owns(preliminary.AgentId)) return null;
        return await _workflow.Atomic(async () =>
        {
            await _workflow.LockProperty(preliminary.PropertyId, token);
            var ids = finalPrice.HasValue
                ? await _context.Opportunities.Where(o => o.PropertyId == preliminary.PropertyId && o.Status == "Open").Select(o => o.Id).ToListAsync(token)
                : [];
            foreach (var opportunityId in ids.Append(id).Distinct().Order()) await _workflow.LockOpportunity(opportunityId, token);
            var target = await _context.Opportunities.AsNoTracking().Include(o => o.Stage).SingleOrDefaultAsync(o => o.Id == id, token);
            if (target is null || !_access.Owns(target.AgentId)) return null;
            if (target.PropertyId != preliminary.PropertyId) throw AssociationConflict();
            if (target.Status != "Open") throw new WorkflowException(409, "This opportunity is already closed. Reload before trying again.");
            var property = await _context.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == target.PropertyId, token)
                ?? throw AssociationConflict();
            var exclusiveIds = await _context.Opportunities.Where(o => o.PropertyId == property.Id && o.Status == "Open"
                && (o.Stage.Code == "RESERVATION" || o.Stage.Code == "CLOSING")).Select(o => o.Id).ToListAsync(token);
            var soleHolder = Exclusive(target.Stage.Code) && exclusiveIds.Count == 1 && exclusiveIds[0] == id;
            if (finalPrice.HasValue)
            {
                if (target.Stage.Code != "CLOSING") throw WorkflowException.Invalid(nameof(Opportunity.StageId), "Move the opportunity to Closing before marking it Won.");
                if (property.Status != "Reserved" || !soleHolder) throw ReservationConflict();
            }
            var competitors = finalPrice.HasValue
                ? await _context.Opportunities.AsNoTracking().Where(o => o.PropertyId == property.Id && o.Status == "Open" && o.Id != id).OrderBy(o => o.Id).ToListAsync(token)
                : [];
            var affectedContacts = competitors.Select(o => o.BuyerContactId).Append(target.BuyerContactId).Append(property.SellerContactId).Distinct().Order().ToArray();
            await _workflow.LockContacts(affectedContacts, token);
            var oldPropertyStatus = property.Status;
            var propertyStatus = finalPrice.HasValue ? "Sold" : property.Status == "Reserved" && soleHolder ? "Available" : property.Status;
            var now = DateTime.UtcNow;
            if (oldPropertyStatus != propertyStatus)
                await _context.Properties.Where(p => p.Id == property.Id).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, propertyStatus).SetProperty(p => p.UpdatedAt, now), token);
            await SaveOutcome(target, finalPrice.HasValue ? "Won" : "Lost", finalPrice, lostReason, false, oldPropertyStatus, propertyStatus, now, token);
            foreach (var competitor in competitors)
                await SaveOutcome(competitor, "Lost", null, "Property sold", true, oldPropertyStatus, propertyStatus, now, token);
            await _workflow.RefreshContacts(affectedContacts, token);
            return await GetByIdAsync(id, cancellationToken: token);
        }, token);
    }

    private async Task SaveOutcome(Opportunity previous, string status, decimal? finalPrice, string? lostReason, bool automatic,
        string previousPropertyStatus, string propertyStatus, DateTime now, CancellationToken token)
    {
        _workflow.Detach<Opportunity>(previous.Id);
        var persisted = await _context.Opportunities.SingleAsync(o => o.Id == previous.Id, token);
        persisted.Status = status; persisted.FinalPrice = finalPrice; persisted.LostReason = lostReason; persisted.ClosedAt = now;
        _context.Activities.Add(new Activity
        {
            Type = "System", Subject = status == "Won" ? "Opportunity won" : automatic ? "Opportunity automatically lost — property sold" : "Opportunity lost",
            ContactId = previous.BuyerContactId, OpportunityId = previous.Id, PropertyId = previous.PropertyId,
            UserId = previous.AgentId, CreatedById = _access.ActorId, IsSystemGenerated = true, CompletedAt = now,
            Details = JsonSerializer.Serialize(new
            {
                reason = status == "Won" ? "Opportunity marked Won" : automatic ? "Property sold" : lostReason,
                previousStatus = previous.Status, status, previousFinalPrice = previous.FinalPrice, finalPrice,
                previousLostReason = previous.LostReason, lostReason, previousClosedAt = previous.ClosedAt, closedAt = now,
                previousPropertyStatus, propertyStatus, commissionRate = previous.CommissionRate,
                commissionAmount = status == "Won" && finalPrice.HasValue ? (decimal?)decimal.Round(finalPrice.Value * previous.CommissionRate, 2, MidpointRounding.AwayFromZero) : null,
                automatic
            })
        });
        await _context.SaveChangesAsync(token);
    }

    public async Task<Opportunity?> SetCommissionRateAsync(int id, decimal commissionRate, CancellationToken cancellationToken = default)
    {
        if (!_access.IsManager) throw new WorkflowException(403, "Only Manager/Admin may edit commission rates.");
        if (commissionRate < 0 || commissionRate > 1 || commissionRate != decimal.Round(commissionRate, 6))
            throw WorkflowException.Invalid(nameof(Opportunity.CommissionRate), "Use a decimal rate from zero to one with at most six decimal places.");
        var preliminary = await _context.Opportunities.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (preliminary is null || !_access.Owns(preliminary.AgentId)) return null;
        return await _workflow.Atomic(async () =>
        {
            await _workflow.LockProperty(preliminary.PropertyId, cancellationToken);
            await _workflow.LockOpportunity(id, cancellationToken);
            var existing = await _context.Opportunities.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
            if (existing is null || !_access.Owns(existing.AgentId)) return null;
            if (existing.PropertyId != preliminary.PropertyId) throw AssociationConflict();
            if (existing.Status != "Open") throw new WorkflowException(409, "Commission rates can only be changed on Open opportunities.");
            if (existing.CommissionRate != commissionRate)
            {
                await _workflow.LockContacts([existing.BuyerContactId], cancellationToken);
                await _context.Opportunities.Where(o => o.Id == id).ExecuteUpdateAsync(set => set.SetProperty(o => o.CommissionRate, commissionRate).SetProperty(o => o.UpdatedAt, DateTime.UtcNow), cancellationToken);
                await _workflow.History("Commission rate changed", existing.BuyerContactId, existing.AgentId,
                    new { reason = "Commission rate changed", previousCommissionRate = existing.CommissionRate, commissionRate }, cancellationToken, existing.PropertyId, id);
            }
            return await GetByIdAsync(id, cancellationToken: cancellationToken);
        }, cancellationToken);
    }

    public override async Task<IEnumerable<Opportunity>> GetAllAsync(
        IEnumerable<Expression<Func<Opportunity, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        var entities = await Query(includes).ToListAsync(cancellationToken);
        foreach (var entity in entities) Redact(entity);
        return entities;
    }

    public override async Task<Opportunity?> GetByIdAsync(
        int id,
        IEnumerable<Expression<Func<Opportunity, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        var entity = await Query(includes).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (entity is null) return null;
        Redact(entity);
        return entity;
    }
    private void Redact(Opportunity entity)
    { if (entity.BuyerContact is not null && !_access.Owns(entity.BuyerContact.OwnerId)) entity.BuyerContact = null!; }
    private IQueryable<Opportunity> Query(IEnumerable<Expression<Func<Opportunity, object>>>? includes)
    {
        var actor = _access.ActorId; var manager = _access.IsManager;
        IQueryable<Opportunity> query = _dbSet.AsNoTracking().Where(o => manager || o.AgentId == actor);
        foreach (var include in includes ?? _defaultIncludes) query = query.Include(include);
        return query;
    }
    public override async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        await GetByIdAsync(id, cancellationToken: cancellationToken) is not null && await base.DeleteAsync(id, cancellationToken);
}

public class ActivityService : CrudService<Activity>, IActivityService
{
    private readonly StaffAccess _access;
    public ActivityService(ApplicationDbContext context, StaffAccess access) : base(context) { _access = access; }
    private IQueryable<Activity> Query()
    {
        var actor = _access.ActorId; var manager = _access.IsManager;
        return _dbSet.AsNoTracking().Where(a => manager
            || (a.OpportunityId != null && a.Opportunity!.AgentId == actor)
            || (a.OpportunityId == null && a.Contact.OwnerId == actor && (a.CreatedById == actor || a.UserId == actor)));
    }
    public override async Task<IEnumerable<Activity>> GetAllAsync(IEnumerable<Expression<Func<Activity, object>>>? includes = null,
        CancellationToken cancellationToken = default) => await Query().ToListAsync(cancellationToken);
    public override Task<Activity?> GetByIdAsync(int id, IEnumerable<Expression<Func<Activity, object>>>? includes = null,
        CancellationToken cancellationToken = default) => Query().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
    public override async Task<Activity> CreateAsync(Activity entity, CancellationToken cancellationToken = default)
    {
        if (!await _access.ActivityTargetVisible(entity, cancellationToken))
            throw WorkflowException.Invalid(nameof(Activity.ContactId), "Choose a visible activity target.");
        entity.CreatedById = _access.ActorId;
        return await base.CreateAsync(entity, cancellationToken);
    }
    public override async Task<Activity?> UpdateAsync(int id, Activity entity, CancellationToken cancellationToken = default)
    {
        if (await GetByIdAsync(id, cancellationToken: cancellationToken) is null) return null;
        if (!await _access.ActivityTargetVisible(entity, cancellationToken))
            throw WorkflowException.Invalid(nameof(Activity.ContactId), "Choose a visible activity target.");
        return await base.UpdateAsync(id, entity, cancellationToken);
    }
    public override async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        await GetByIdAsync(id, cancellationToken: cancellationToken) is not null && await base.DeleteAsync(id, cancellationToken);
}

public class NoteService : CrudService<Note>, INoteService
{
    private static readonly List<Expression<Func<Note, object>>> _defaultIncludes = new()
    {
        n => n.Author
    };

    private readonly StaffAccess _access;
    public NoteService(ApplicationDbContext context, StaffAccess access) : base(context) { _access = access; }
    private IQueryable<Note> Query(IEnumerable<Expression<Func<Note, object>>>? includes)
    {
        var actor = _access.ActorId; var manager = _access.IsManager;
        IQueryable<Note> query = _dbSet.AsNoTracking().Where(n =>
            n.EntityType == "Contact" && _context.Contacts.Any(c => c.Id == n.EntityId && (manager || c.OwnerId == actor))
            || n.EntityType == "Opportunity" && _context.Opportunities.Any(o => o.Id == n.EntityId && (manager || o.AgentId == actor))
            || n.EntityType == "Property" && _context.Properties.Any(p => p.Id == n.EntityId));
        foreach (var include in includes ?? _defaultIncludes) query = query.Include(include);
        return query;
    }

    public override async Task<IEnumerable<Note>> GetAllAsync(
        IEnumerable<Expression<Func<Note, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        return await Query(includes).ToListAsync(cancellationToken);
    }

    public override async Task<Note?> GetByIdAsync(
        int id,
        IEnumerable<Expression<Func<Note, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        return await Query(includes).SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
    }

    public override async Task<Note> CreateAsync(Note entity, CancellationToken cancellationToken = default)
    {
        if (!await _access.TargetVisible(entity.EntityType, entity.EntityId, cancellationToken))
            throw WorkflowException.Invalid(nameof(Note.EntityId), "Choose a visible note target.");
        if (entity.AuthorId != _access.ActorId) throw new WorkflowException(403, "The note author must be the authenticated user.");
        return await base.CreateAsync(entity, cancellationToken);
    }
    public override async Task<Note?> UpdateAsync(int id, Note entity, CancellationToken cancellationToken = default)
    {
        var existing = await GetByIdAsync(id, cancellationToken: cancellationToken);
        if (existing is null) return null;
        if (existing.AuthorId != _access.ActorId && !_access.IsManager) throw new WorkflowException(403, "Only the author or Manager/Admin may edit this note.");
        return await base.UpdateAsync(id, entity, cancellationToken);
    }
    public override async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var existing = await GetByIdAsync(id, cancellationToken: cancellationToken);
        if (existing is null) return false;
        if (existing.AuthorId != _access.ActorId && !_access.IsManager) throw new WorkflowException(403, "Only the author or Manager/Admin may delete this note.");
        return await base.DeleteAsync(id, cancellationToken);
    }
}

public class PropertyService : CrudService<Property>, IPropertyService
{
    private static readonly List<Expression<Func<Property, object>>> _defaultIncludes = new()
    {
        p => p.SellerContact!,
        p => p.ListingAgent
    };

    private readonly StaffAccess _access;
    private readonly Part01Workflow _workflow;
    public PropertyService(ApplicationDbContext context, StaffAccess access, Part01Workflow workflow) : base(context)
    { _access = access; _workflow = workflow; }

    public override async Task<Property> CreateAsync(Property entity, CancellationToken cancellationToken = default)
    {
        if (entity.Status != "Draft") throw WorkflowException.Invalid(nameof(Property.Status), "New properties must start as Draft.");
        if (entity.ListingAgentId == 0 && !_access.IsManager) entity.ListingAgentId = _access.ActorId;
        _access.Assignment(entity.ListingAgentId, _access.ActorId, nameof(Property.ListingAgentId));
        await Validate(entity, null, cancellationToken);
        return await _workflow.Atomic(async () =>
        {
            await _workflow.LockContacts([entity.SellerContactId], cancellationToken);
            var created = await base.CreateAsync(entity, cancellationToken);
            await _workflow.RefreshContacts([entity.SellerContactId], cancellationToken);
            return created;
        }, cancellationToken);
    }

    public override async Task<Property?> UpdateAsync(int id, Property entity, CancellationToken cancellationToken = default)
    {
        return await _workflow.Atomic(async () =>
        {
            if (await _workflow.LockProperty(id, cancellationToken) == 0) return null;
            var existing = await _context.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (existing is null || !_access.Owns(existing.ListingAgentId)) return null;
            var allowed = existing.Status switch
            {
                "Draft" => entity.Status is "Draft" or "Available" or "Withdrawn",
                "Available" => entity.Status is "Available" or "Withdrawn",
                "Withdrawn" => entity.Status is "Withdrawn" or "Available",
                _ => false
            };
            if (!allowed) throw WorkflowException.Invalid(nameof(Property.Status), "Use the supported Draft, Available or Withdrawn listing lifecycle.");
            if (entity.ListingAgentId != existing.ListingAgentId && !_access.IsManager)
                throw new WorkflowException(403, "Only Manager/Admin may reassign a listing.", nameof(Property.ListingAgentId));
            await Validate(entity, existing, cancellationToken);
            await _workflow.LockContacts([existing.SellerContactId, entity.SellerContactId], cancellationToken);
            _workflow.Detach<Property>(id);
            await base.UpdateAsync(id, entity, cancellationToken);
            if (existing.Price != entity.Price)
                await _workflow.History("Property price changed", entity.SellerContactId, entity.ListingAgentId,
                    new { previousPrice = existing.Price, price = entity.Price }, cancellationToken, id);
            if (existing.ListingAgentId != entity.ListingAgentId)
                await _workflow.History("Listing Agent reassigned", entity.SellerContactId, entity.ListingAgentId,
                    new { previousListingAgentId = existing.ListingAgentId, listingAgentId = entity.ListingAgentId }, cancellationToken, id);
            await _workflow.RefreshContacts([existing.SellerContactId, entity.SellerContactId], cancellationToken);
            return await GetByIdAsync(id, cancellationToken: cancellationToken);
        }, cancellationToken);
    }

    private async Task Validate(Property entity, Property? existing, CancellationToken token)
    {
        var seller = await _context.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == entity.SellerContactId, token);
        if (seller is null || seller.Type is not ("Seller" or "Both") || (existing?.SellerContactId != seller.Id && !_access.Owns(seller.OwnerId)))
            throw WorkflowException.Invalid(nameof(Property.SellerContactId), "Choose a visible Seller or Both contact.");
        await _access.ActiveAgent(entity.ListingAgentId, nameof(Property.ListingAgentId), token);
        if (entity.Status == "Available" && !(entity.Price > 0))
            throw WorkflowException.Invalid(nameof(Property.Price), "Publication requires a price greater than zero.");
    }

    public override async Task<IEnumerable<Property>> GetAllAsync(
        IEnumerable<Expression<Func<Property, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        _ = _access.ActorId;
        var entities = (await base.GetAllAsync(includes, cancellationToken)).ToList();
        foreach (var entity in entities) Redact(entity);
        return entities;
    }

    public override async Task<Property?> GetByIdAsync(
        int id,
        IEnumerable<Expression<Func<Property, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        includes ??= _defaultIncludes;
        _ = _access.ActorId;
        var entity = await base.GetByIdAsync(id, includes, cancellationToken);
        if (entity is not null) Redact(entity);
        return entity;
    }
    private void Redact(Property entity)
    { if (entity.SellerContact is not null && !_access.Owns(entity.SellerContact.OwnerId)) entity.SellerContact = null!; }
    public override async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _workflow.Atomic(async () =>
        {
            if (await _workflow.LockProperty(id, cancellationToken) == 0) return false;
            var entity = await _context.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (entity is null || !_access.Owns(entity.ListingAgentId)) return false;
            _workflow.Detach<Property>(id);
            var deleted = await base.DeleteAsync(id, cancellationToken);
            await _workflow.RefreshContacts([entity.SellerContactId], cancellationToken);
            return deleted;
        }, cancellationToken);
    }
}



