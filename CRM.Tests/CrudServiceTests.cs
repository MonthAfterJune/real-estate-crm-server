using System.Linq.Expressions;
using CRM.Data;
using CRM.Models;
using CRM.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRM.Tests;

public class CrudServiceTests
{
    [Fact]
    public async Task Generic_update_preserves_audits_and_server_managed_outcomes()
    {
        var created = DateTime.UtcNow.AddDays(-10);
        var existing = new Opportunity { Id = 42, Status = "Open", CommissionRate = .025m, CreatedAt = created,
            BuyerContactId = 1, PropertyId = 2, AgentId = 3, StageId = 1 };
        var updated = await Update(existing, new Opportunity { Id = 999, Status = "Won", CommissionRate = .9m,
            FinalPrice = 100000m, ClosedAt = DateTime.UtcNow, IsDeleted = true,
            BuyerContactId = 1, PropertyId = 2, AgentId = 3, StageId = 2 });
        Assert.Equal(created, updated.CreatedAt);
        Assert.False(updated.IsDeleted);
        Assert.Equal("Open", updated.Status);
        Assert.Equal(.025m, updated.CommissionRate);
        Assert.Null(updated.FinalPrice);
        Assert.Null(updated.ClosedAt);
        Assert.Equal(2, updated.StageId);
    }

    [Fact]
    public async Task Generic_update_preserves_activity_author_and_generated_history_fields()
    {
        var existing = new Activity { Id = 42, CreatedById = 1, UserId = 2, Subject = "Before", Type = "Call" };
        var updated = await Update(existing, new Activity { Subject = "After", Type = "Call", CreatedById = 999,
            IsSystemGenerated = true, FromStageId = 1, ToStageId = 2 });
        Assert.Equal(1, updated.CreatedById);
        Assert.False(updated.IsSystemGenerated);
        Assert.Null(updated.FromStageId);
        Assert.Null(updated.ToStageId);
        Assert.Equal("After", updated.Subject);
    }

    [Fact]
    public async Task Contact_update_preserves_route_id_when_request_has_no_id()
    {
        var result = await Update(new Contact { Id = 42, Type = "Buyer", FirstName = "Before", LastName = "Lee" },
            new Contact { Type = "Seller", FirstName = "After", LastName = "Lee" });
        Assert.Equal(42, result.Id);
        Assert.Equal("Seller", result.Type);
        Assert.Equal("After", result.FirstName);
    }

    [Fact]
    public async Task Property_update_preserves_id_and_changes_required_assignments()
    {
        var result = await Update(new Property
        {
            Id = 42, Address = "1 Main St", Price = 500000m, Status = "Available", Bedrooms = 3, SellerContactId = 5, ListingAgentId = 1
        }, new Property
        {
            Address = "2 Main St", Price = 490000m, Status = "Reserved", ListingAgentId = 2, SellerContactId = 6, Bedrooms = 0
        });
        Assert.Equal(42, result.Id);
        Assert.Equal("Reserved", result.Status);
        Assert.Equal(490000m, result.Price);
        Assert.Equal(2, result.ListingAgentId);
        Assert.Equal(6, result.SellerContactId);
        Assert.Equal(0, result.Bedrooms);
    }

    [Fact]
    public async Task Contact_update_preserves_new_lifecycle_and_owner_fields()
    {
        var result = await Update(new Contact { Id = 42, Type = "Buyer", FirstName = "Alex", OwnerId = 1 },
            new Contact { Type = "Buyer", FirstName = "Alex", OwnerId = 2, Status = "Active", Source = "Website" });
        Assert.Equal((42, 2, "Active", "Website"), (result.Id, result.OwnerId, result.Status, result.Source));
    }
    [Fact]
    public async Task Opportunity_update_uses_new_fields_and_ignores_incoming_id()
    {
        var result = await Update(new Opportunity
        {
            Id = 42, BuyerContactId = 1, PropertyId = 2, AgentId = 3, StageId = 4, OfferAmount = 100m
        }, new Opportunity
        {
            Id = 999, BuyerContactId = 5, PropertyId = 6, AgentId = 7, StageId = 8, OfferAmount = 450000.25m
        });
        Assert.Equal(42, result.Id);
        Assert.Equal((5, 6, 7, 8, (decimal?)450000.25m),
            (result.BuyerContactId, result.PropertyId, result.AgentId, result.StageId, result.OfferAmount));
    }

    [Fact]
    public async Task Create_returns_reloaded_relationships_and_passes_cancellation_token()
    {
        using var context = new TrackingContext();
        var property = new Property { Id = 42, Address = "1 Main St", Status = "Available", ListingAgentId = 2 };
        var reloaded = new Property
        {
            Id = 42, Address = "1 Main St", Status = "Available", ListingAgentId = 2,
            ListingAgent = new User { Id = 2, FullName = "Taylor Agent" }
        };
        var service = new ReloadingService<Property>(context, reloaded);
        using var cancellation = new CancellationTokenSource();

        var result = await service.CreateAsync(property, cancellation.Token);

        Assert.Same(reloaded, result);
        Assert.Equal("Taylor Agent", result.ListingAgent.FullName);
        Assert.Equal(42, service.ReloadedId);
        Assert.Equal(cancellation.Token, service.ReloadToken);
        Assert.Equal(cancellation.Token, context.SaveToken);
        Assert.Equal(1, context.SaveCount);
    }

    [Fact]
    public async Task Update_returns_reloaded_relationships()
    {
        using var context = new TrackingContext();
        context.Attach(new Contact { Id = 42, FirstName = "Alex", Type = "Buyer", OwnerId = 1 });
        var reloaded = new Contact { Id = 42, FirstName = "Alex", Type = "Buyer", OwnerId = 1, Owner = new User { Id = 1, FullName = "Taylor Agent" } };
        var service = new ReloadingService<Contact>(context, reloaded);
        var result = await service.UpdateAsync(42, new Contact { FirstName = "Alex", Type = "Buyer", OwnerId = 1 });
        Assert.Same(reloaded, result);
        Assert.Equal("Taylor Agent", result!.Owner.FullName);
    }

    private static async Task<T> Update<T>(T existing, T incoming) where T : class
    {
        using var context = new TrackingContext();
        context.Attach(existing);
        var service = new ReloadingService<T>(context, existing);

        var updated = await service.UpdateAsync(42, incoming);

        Assert.Same(existing, updated);
        Assert.Equal(1, context.SaveCount);
        Assert.Equal(42, service.ReloadedId);
        return updated!;
    }

    // Exercise EF's real change tracker (including immutable keys) without writing to a database.
    private sealed class TrackingContext : ApplicationDbContext
    {
        public int SaveCount { get; private set; }
        public CancellationToken SaveToken { get; private set; }

        public TrackingContext() : base(SchemaTests.Options()) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChangeTracker.DetectChanges();
            ChangeTracker.AcceptAllChanges();
            SaveCount++;
            SaveToken = cancellationToken;
            return Task.FromResult(1);
        }
    }

    private sealed class ReloadingService<T> : CrudService<T> where T : class
    {
        private readonly T _reloaded;
        public int? ReloadedId { get; private set; }
        public CancellationToken ReloadToken { get; private set; }

        public ReloadingService(ApplicationDbContext context, T reloaded) : base(context) => _reloaded = reloaded;

        public override Task<T?> GetByIdAsync(int id,
            IEnumerable<Expression<Func<T, object>>>? includes = null, CancellationToken cancellationToken = default)
        {
            ReloadedId = id;
            ReloadToken = cancellationToken;
            return Task.FromResult<T?>(_reloaded);
        }
    }
}

