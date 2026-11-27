using System.Linq.Expressions;

namespace CRM.Services.Abstractions;

public interface ICrudService<TEntity>
    where TEntity : class
{
    Task<IEnumerable<TEntity>> GetAllAsync(
        IEnumerable<Expression<Func<TEntity, object>>>? includes = null,
        CancellationToken cancellationToken = default);

    Task<TEntity?> GetByIdAsync(
        int id,
        IEnumerable<Expression<Func<TEntity, object>>>? includes = null,
        CancellationToken cancellationToken = default);

    Task<TEntity> CreateAsync(TEntity entity, CancellationToken cancellationToken = default);

    Task<TEntity?> UpdateAsync(int id, TEntity entity, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
