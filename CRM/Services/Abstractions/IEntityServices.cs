using CRM.Models;
using CRM.Services.Abstractions;

namespace CRM.Services.Abstractions;

public interface IRoleService : ICrudService<Role>
{
}

public interface IUserService : ICrudService<User>
{
}

public interface IContactService : ICrudService<Contact>
{
}

public interface IPipelineStageService : ICrudService<PipelineStage>
{
}

public interface IOpportunityService : ICrudService<Opportunity>
{
    Task<Opportunity?> MarkWonAsync(int id, decimal finalPrice, CancellationToken cancellationToken = default);
    Task<Opportunity?> MarkLostAsync(int id, string lostReason, CancellationToken cancellationToken = default);
    Task<Opportunity?> SetCommissionRateAsync(int id, decimal commissionRate, CancellationToken cancellationToken = default);
    Task<Opportunity?> ReopenAsync(int id, string reason, CancellationToken cancellationToken = default);
}

public interface IActivityService : ICrudService<Activity>
{
}

public interface INoteService : ICrudService<Note>
{
}

public interface IPropertyService : ICrudService<Property>
{
}

