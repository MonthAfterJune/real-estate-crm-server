using System.Linq.Expressions;
using CRM.Data;
using CRM.Models;
using CRM.Services.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CRM.Services;

public class CrudService<TEntity> : ICrudService<TEntity>
    where TEntity : class
{
    protected readonly ApplicationDbContext _context;
    protected readonly DbSet<TEntity> _dbSet;

    public CrudService(ApplicationDbContext context)
    {
        _context = context;
        _dbSet = _context.Set<TEntity>();
    }

    public virtual async Task<IEnumerable<TEntity>> GetAllAsync(
        IEnumerable<Expression<Func<TEntity, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AsNoTracking();

        if (includes != null)
        {
            query = includes.Aggregate(query, (current, include) => current.Include(include));
        }

        return await query.ToListAsync(cancellationToken);
    }

    public virtual async Task<TEntity?> GetByIdAsync(
        int id,
        IEnumerable<Expression<Func<TEntity, object>>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AsNoTracking();

        if (includes != null)
        {
            query = includes.Aggregate(query, (current, include) => current.Include(include));
        }

        var keyProperty = _context.Model
            .FindEntityType(typeof(TEntity))?
            .FindPrimaryKey()?
            .Properties
            .First();

        if (keyProperty == null)
        {
            return await _dbSet.FindAsync(new object?[] { id }, cancellationToken);
        }

        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var propertyAccess = Expression.Property(parameter, keyProperty.PropertyInfo!);
        var constant = Expression.Constant(id);
        var equals = Expression.Equal(propertyAccess, constant);
        var lambda = Expression.Lambda<Func<TEntity, bool>>(equals, parameter);

        return await query.FirstOrDefaultAsync(lambda, cancellationToken);
    }

    public virtual async Task<TEntity> CreateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        var entry = _dbSet.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        var key = entry.Metadata.FindPrimaryKey()!.Properties.Single();
        var id = (int)entry.Property(key.Name).CurrentValue!;
        // Use the same includes as GET so write responses contain related records.
        return await GetByIdAsync(id, cancellationToken: cancellationToken) ?? entity;
    }

    public virtual async Task<TEntity?> UpdateAsync(int id, TEntity entity, CancellationToken cancellationToken = default)
    {
        var existing = await _dbSet.FindAsync(new object?[] { id }, cancellationToken);
        if (existing == null || existing is IAuditableEntity { IsDeleted: true })
        {
            return null;
        }

        EnsureEditable(existing);
        var entry = _context.Entry(existing);
        var incomingValues = _context.Entry(entity).CurrentValues;
        foreach (var property in entry.Properties.Where(p => !p.Metadata.IsPrimaryKey()))
        {
            if (IsServerManaged(existing, property.Metadata.Name)) continue;
            // Request DTOs do not contain the key; the route ID must remain authoritative.
            property.CurrentValue = incomingValues[property.Metadata.Name];
        }

        await _context.SaveChangesAsync(cancellationToken);
        return await GetByIdAsync(id, cancellationToken: cancellationToken) ?? existing;
    }

    public virtual async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbSet.FindAsync(new object?[] { id }, cancellationToken);
        if (entity == null || entity is IAuditableEntity { IsDeleted: true })
        {
            return false;
        }

        if (entity is User user)
        {
            user.IsActive = false;
            user.SecurityStamp = Guid.NewGuid().ToString();
        }
        else if (entity is IAuditableEntity audited)
        {
            await EnsureCanSoftDelete(entity, id, cancellationToken);
            audited.IsDeleted = true;
        }
        else
        {
            _dbSet.Remove(entity);
        }
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool IsServerManaged(TEntity entity, string name)
    {
        if (entity is IAuditableEntity && name is nameof(IAuditableEntity.CreatedAt) or nameof(IAuditableEntity.UpdatedAt) or nameof(IAuditableEntity.IsDeleted)) return true;
        if (entity is Opportunity && name is nameof(Opportunity.Status) or nameof(Opportunity.FinalPrice) or nameof(Opportunity.CommissionRate) or nameof(Opportunity.LostReason) or nameof(Opportunity.ClosedAt)) return true;
        if (entity is Activity && name is nameof(Activity.CreatedById) or nameof(Activity.IsSystemGenerated) or nameof(Activity.FromStageId) or nameof(Activity.ToStageId)) return true;
        if (entity is PipelineStage && name is nameof(PipelineStage.Code) or nameof(PipelineStage.SortOrder)) return true;
        if (entity is Note && name is nameof(Note.AuthorId) or nameof(Note.EntityType) or nameof(Note.EntityId)) return true;
        return false;
    }

    private static void EnsureEditable(TEntity entity)
    {
        if (entity is Activity { IsSystemGenerated: true }) throw new BadHttpRequestException("Generated history cannot be changed.");
        if (entity is Opportunity { Status: not "Open" }) throw new BadHttpRequestException("Closed opportunities cannot be changed through generic updates.");
        if (entity is Property { Status: "Sold" }) throw new BadHttpRequestException("Sold properties are locked.");
        if (entity is Note note && note.CreatedAt < DateTime.UtcNow.AddHours(-24)) throw new BadHttpRequestException("Add a new note to correct retained history.");
    }

    private async Task EnsureCanSoftDelete(TEntity entity, int id, CancellationToken token)
    {
        var kind = typeof(TEntity).Name;
        var attachments = await _context.Notes.IgnoreQueryFilters().AnyAsync(n => n.EntityType == kind && n.EntityId == id, token)
            || await _context.Documents.IgnoreQueryFilters().AnyAsync(d => d.EntityType == kind && d.EntityId == id, token);
        var retained = entity switch
        {
            Contact => await _context.Properties.IgnoreQueryFilters().AnyAsync(p => p.SellerContactId == id, token)
                || await _context.Opportunities.IgnoreQueryFilters().AnyAsync(o => o.BuyerContactId == id, token)
                || await _context.Activities.IgnoreQueryFilters().AnyAsync(a => a.ContactId == id, token),
            Property p => p.Status is "Reserved" or "Sold"
                || await _context.Opportunities.IgnoreQueryFilters().AnyAsync(o => o.PropertyId == id, token)
                || await _context.Activities.IgnoreQueryFilters().AnyAsync(a => a.PropertyId == id, token),
            Opportunity o => o.Status == "Open" || await _context.Activities.IgnoreQueryFilters().AnyAsync(a => a.OpportunityId == id, token),
            Activity a => a.IsSystemGenerated,
            _ => false
        };
        if (attachments || retained) throw new BadHttpRequestException("Retained references or record status prevent deletion.");
    }
}
