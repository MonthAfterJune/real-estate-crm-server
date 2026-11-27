using System.Security.Claims;
using System.Text.Json;
using CRM.Data;
using CRM.Models;
using Microsoft.EntityFrameworkCore;

namespace CRM.Services;

public sealed class WorkflowException(int status, string detail, string? field = null, int? existingContactId = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string? Field { get; } = field;
    public int? ExistingContactId { get; } = existingContactId;
    public static WorkflowException Invalid(string field, string detail) => new(400, detail, field);
}

public static class ContactChannels
{
    public static string? Email(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    public static string? Phone(string? value) => string.IsNullOrWhiteSpace(value) ? null
        : string.Concat(value.Trim().Where(c => !char.IsWhiteSpace(c) && c is not '(' and not ')' and not '-' and not '.'));
}

// Request identity is explicit. Background/test callers must supply an actor rather than acquire wider access.
public sealed class StaffAccess(ApplicationDbContext db, IHttpContextAccessor accessor)
{
    public static int? DefaultAgent(ClaimsPrincipal principal) => principal.IsInRole("agent")
        && int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : null;
    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? new ClaimsPrincipal();
    public int ActorId => int.TryParse(Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0
        && (Principal.IsInRole("agent") || IsManager) ? id : throw new WorkflowException(401, "A staff session is required.");
    public bool IsManager => Principal.IsInRole("manager") || Principal.IsInRole("admin");
    public bool Owns(int ownerId) => ActorId == ownerId || IsManager;
    public Task<bool> ContactVisible(int id, CancellationToken token) =>
        db.Contacts.AnyAsync(c => c.Id == id && (IsManager || c.OwnerId == ActorId), token);
    public Task<bool> OpportunityVisible(int id, CancellationToken token) =>
        db.Opportunities.AnyAsync(o => o.Id == id && (IsManager || o.AgentId == ActorId), token);
    public async Task<bool> TargetVisible(string kind, int id, CancellationToken token) => kind switch
    {
        "Contact" => await ContactVisible(id, token),
        "Opportunity" => await OpportunityVisible(id, token),
        "Property" => ActorId > 0 && await db.Properties.AnyAsync(p => p.Id == id, token),
        _ => false
    };
    public async Task<bool> ActivityTargetVisible(Activity activity, CancellationToken token)
    {
        if (activity.OpportunityId is int opportunity)
            return await db.Opportunities.AnyAsync(o => o.Id == opportunity
                && (IsManager || o.AgentId == ActorId) && o.BuyerContactId == activity.ContactId
                && (activity.PropertyId == null || o.PropertyId == activity.PropertyId), token);
        return await ContactVisible(activity.ContactId, token)
            && (activity.PropertyId is not int property || await db.Properties.AnyAsync(p => p.Id == property && p.SellerContactId == activity.ContactId, token));
    }
    public async Task ActiveAgent(int id, string field, CancellationToken token)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id && u.IsActive && u.Role.Name.ToLower() == "agent", token))
            throw WorkflowException.Invalid(field, "Choose an active Agent.");
    }
    public void Assignment(int actual, int permitted, string field)
    {
        if (!IsManager && actual != permitted) throw new WorkflowException(403, "Only Manager/Admin may choose a different Agent.", field);
    }
}

public sealed class Part01Workflow(ApplicationDbContext db, StaffAccess access)
{
    // A no-op UPDATE obtains a database row lock without SaveChanges audit stamping.
    // Every workflow writing a listing participates in this protocol.
    public Task<int> LockProperty(int id, CancellationToken token) => db.Properties.Where(p => p.Id == id)
        .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, p => p.Status), token);

    public Task<int> LockOpportunity(int id, CancellationToken token) => db.Opportunities.Where(o => o.Id == id)
        .ExecuteUpdateAsync(set => set.SetProperty(o => o.Status, o => o.Status), token);

    public async Task LockContacts(IEnumerable<int> ids, CancellationToken token)
    {
        foreach (var id in ids.Distinct().Order())
            if (await db.Contacts.Where(c => c.Id == id).ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, c => c.Status), token) == 0)
                throw new WorkflowException(409, "A related contact is no longer available. Reload before saving.");
    }

    public void Detach<TEntity>(int id) where TEntity : AuditableEntity
    {
        foreach (var entry in db.ChangeTracker.Entries<TEntity>().Where(e => e.Entity.Id == id).ToList())
            entry.State = EntityState.Detached;
    }

    public async Task<T> Atomic<T>(Func<Task<T>> action, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is not null) return await action();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var result = await action();
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task RefreshContacts(IEnumerable<int> ids, CancellationToken token)
    {
        var affected = ids.Distinct().Order().ToArray();
        await LockContacts(affected, token);
        var now = DateTime.UtcNow;
        // A conditional, set-based refresh never writes stale identity/owner/manual-override fields.
        foreach (var id in affected)
        {
            var updated = await db.Contacts.Where(c => c.Id == id).ExecuteUpdateAsync(set => set
                .SetProperty(c => c.Status, c => c.IsManuallyInactive ? "Inactive"
                    : db.Opportunities.Any(o => o.BuyerContactId == c.Id && o.Status == "Won") || db.Properties.Any(p => p.SellerContactId == c.Id && p.Status == "Sold") ? "Client"
                    : db.Opportunities.Any(o => o.BuyerContactId == c.Id && o.Status == "Open") || db.Properties.Any(p => p.SellerContactId == c.Id && (p.Status == "Available" || p.Status == "Reserved")) ? "Active" : "Lead")
                .SetProperty(c => c.UpdatedAt, now), token);
            if (updated != 1)
                throw new WorkflowException(409, "A related contact is no longer available. Reload before saving.");
        }
    }

    public async Task History(string subject, int contactId, int responsibleId, object details, CancellationToken token,
        int? propertyId = null, int? opportunityId = null)
    {
        db.Activities.Add(new Activity
        {
            Type = "System", Subject = subject, Details = JsonSerializer.Serialize(details), ContactId = contactId,
            UserId = responsibleId, CreatedById = access.ActorId, PropertyId = propertyId, OpportunityId = opportunityId,
            IsSystemGenerated = true, CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(token);
    }
}
