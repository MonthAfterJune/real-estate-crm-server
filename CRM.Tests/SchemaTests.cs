using CRM.Data;
using CRM.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CRM.Tests;

public class SchemaTests
{
    internal static DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=crm_schema_tests;Username=test;Password=test;")
            .Options;

    [Fact]
    public void Model_has_no_accounts_legacy_fields_or_shadow_foreign_keys()
    {
        using var context = new ApplicationDbContext(Options());
        var entities = context.Model.GetEntityTypes().ToList();

        Assert.Equal(12, entities.Count); // Nine ERD entities plus three Identity infrastructure models.
        Assert.Null(context.Model.FindEntityType(typeof(AuditableEntity)));
        Assert.DoesNotContain(entities, e => e.ClrType.Name == "Account");
        Assert.DoesNotContain(entities.SelectMany(e => e.GetProperties()),
            p => p.Name == "AccountId" || p.IsShadowProperty());
        var opportunity = context.Model.FindEntityType(typeof(Opportunity))!;
        Assert.Null(opportunity.FindProperty("Amount"));
        Assert.Null(opportunity.FindProperty("ContactId"));
        Assert.Null(opportunity.FindProperty("OwnerId"));
        Assert.DoesNotContain(entities, e => e.ClrType.Name == "Lead");
        Assert.Null(context.Model.FindEntityType(typeof(Property))!.FindProperty("SellerUserId"));
        Assert.Null(context.Model.FindEntityType(typeof(Activity))!.FindProperty("LeadId"));
    }

    [Theory]
    [InlineData(typeof(Property), "SellerContactId", typeof(Contact), true, "ListedAsSeller")]
    [InlineData(typeof(Contact), "OwnerId", typeof(User), true, "OwnedContacts")]
    [InlineData(typeof(Activity), "ContactId", typeof(Contact), true, "Activities")]
    [InlineData(typeof(Activity), "OpportunityId", typeof(Opportunity), false, "Activities")]
    [InlineData(typeof(Property), "ListingAgentId", typeof(User), true, "ListedProperties")]
    [InlineData(typeof(Opportunity), "BuyerContactId", typeof(Contact), true, "Purchases")]
    [InlineData(typeof(Opportunity), "PropertyId", typeof(Property), true, "Opportunities")]
    [InlineData(typeof(Opportunity), "AgentId", typeof(User), true, "OwnedOpportunities")]
    [InlineData(typeof(Opportunity), "StageId", typeof(PipelineStage), true, "Opportunities")]
    [InlineData(typeof(User), "RoleId", typeof(Role), true, "Users")]
    [InlineData(typeof(Note), "AuthorId", typeof(User), true, "Notes")]
    [InlineData(typeof(Activity), "UserId", typeof(User), true, "Activities")]
    [InlineData(typeof(Activity), "CreatedById", typeof(User), true, "CreatedActivities")]
    [InlineData(typeof(Activity), "PropertyId", typeof(Property), false, "Activities")]
    [InlineData(typeof(Activity), "FromStageId", typeof(PipelineStage), false, "FromActivities")]
    [InlineData(typeof(Activity), "ToStageId", typeof(PipelineStage), false, "ToActivities")]
    [InlineData(typeof(Document), "UploadedById", typeof(User), true, "Documents")]
    public void Relationships_use_expected_targets_nullability_and_delete_behavior(
        Type entity, string foreignKey, Type principal, bool required, string? inverse)
    {
        using var context = new ApplicationDbContext(Options());
        var relationship = Assert.Single(context.Model.FindEntityType(entity)!.GetForeignKeys(),
            fk => fk.Properties.Single().Name == foreignKey);

        Assert.Equal(principal, relationship.PrincipalEntityType.ClrType);
        Assert.Equal(required, relationship.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, relationship.DeleteBehavior);
        Assert.Equal(inverse, relationship.PrincipalToDependent?.Name);
    }

    [Fact]
    public void Model_preserves_precision_lengths_and_unique_email()
    {
        using var context = new ApplicationDbContext(Options());
        foreach (var (entity, name) in new[]
        {
            (typeof(Property), nameof(Property.Price)),
            (typeof(Opportunity), nameof(Opportunity.OfferAmount)),
            (typeof(Opportunity), nameof(Opportunity.FinalPrice))
        })
        {
            var property = context.Model.FindEntityType(entity)!.FindProperty(name)!;
            Assert.Equal(18, property.GetPrecision());
            Assert.Equal(2, property.GetScale());
        }

        foreach (var (entity, name, length) in new[]
        {
            (typeof(Contact), nameof(Contact.Type), 20),
            (typeof(Property), nameof(Property.Address), 200),
            (typeof(Property), nameof(Property.Status), 20)
        })
        {
            var property = context.Model.FindEntityType(entity)!.FindProperty(name)!;
            Assert.False(property.IsNullable);
            Assert.Equal(length, property.GetMaxLength());
        }

        Assert.True(context.Model.FindEntityType(typeof(Property))!
            .FindProperty(nameof(Property.Bedrooms))!.IsNullable);
        Assert.True(Assert.Single(context.Model.FindEntityType(typeof(User))!.GetIndexes(),
            index => index.Properties.Single().Name == nameof(User.Email)).IsUnique);
    }

    [Fact]
    public void Finalized_erd_fields_audit_filters_and_actor_relationships_are_explicit()
    {
        using var context = new ApplicationDbContext(Options());
        var audited = context.Model.GetEntityTypes().Where(e => typeof(IAuditableEntity).IsAssignableFrom(e.ClrType)).ToList();
        Assert.Equal(7, audited.Count);
        Assert.Equal(17, context.Model.GetEntityTypes().Where(e => e.ClrType.Namespace == typeof(Contact).Namespace)
            .Sum(e => e.GetForeignKeys().Count()));
        foreach (var entity in audited)
        {
            Assert.False(entity.FindProperty(nameof(IAuditableEntity.CreatedAt))!.IsNullable);
            Assert.True(entity.FindProperty(nameof(IAuditableEntity.UpdatedAt))!.IsNullable);
            Assert.False(entity.FindProperty(nameof(IAuditableEntity.IsDeleted))!.IsNullable);
            if (entity.ClrType != typeof(User)) Assert.NotNull(entity.GetQueryFilter());
        }
        var user = context.Model.FindEntityType(typeof(User))!;
        Assert.Null(user.FindProperty(nameof(User.Phone))); // Intentional Identity-backed CLR alias.
        Assert.Equal("Phone", user.FindProperty(nameof(User.PhoneNumber))!.GetColumnName());
        Assert.NotNull(user.FindProperty(nameof(User.IsActive)));
        var activity = context.Model.FindEntityType(typeof(Activity))!;
        Assert.Null(activity.FindProperty("OccurredAt"));
        foreach (var name in new[] { "Subject", "Details", "UserId", "CreatedById", "PropertyId", "FromStageId", "ToStageId", "IsSystemGenerated", "ScheduledAt", "CompletedAt" })
            Assert.NotNull(activity.FindProperty(name));
        foreach (var name in new[] { "OfferAmount", "FinalPrice", "LostReason", "ClosedAt" })
            Assert.True(context.Model.FindEntityType(typeof(Opportunity))!.FindProperty(name)!.IsNullable);
    }

    [Fact]
    public void Uniqueness_and_seed_stage_contracts_match_v2()
    {
        using var context = new ApplicationDbContext(Options());
        foreach (var name in new[] { "Email", "Phone" })
        {
            var index = Assert.Single(context.Model.FindEntityType(typeof(Contact))!.GetIndexes(), i => i.Properties.Single().Name == name);
            Assert.True(index.IsUnique);
            Assert.Contains("IsDeleted", index.GetFilter());
        }
        var pair = Assert.Single(context.Model.FindEntityType(typeof(Opportunity))!.GetIndexes(), i => i.Properties.Count == 2);
        Assert.True(pair.IsUnique);
        Assert.Contains("Open", pair.GetFilter());
        var stage = context.Model.FindEntityType(typeof(PipelineStage))!;
        Assert.True(Assert.Single(stage.GetIndexes(), i => i.Properties.Single().Name == "Code").IsUnique);
        Assert.True(Assert.Single(stage.GetIndexes(), i => i.Properties.Single().Name == "SortOrder").IsUnique);
        // Seed metadata is available on the design-time model, not EF's trimmed runtime model.
        var design = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var seeds = design.FindEntityType(typeof(PipelineStage))!.GetSeedData().OrderBy(s => s["SortOrder"]).ToList();
        Assert.Equal(new[] { "INQUIRY", "VIEWING", "OFFER", "NEGOTIATION", "RESERVATION", "CLOSING" }, seeds.Select(s => (string)s["Code"]!));
    }

    [Fact]
    public async Task Audit_fields_are_server_managed_and_deletion_is_soft()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var user = new User { FullName = "Schema Agent", Email = "schema@example.test", Role = new Role { Name = "agent" } };
        var contact = new Contact { FirstName = "Case", LastName = "One", Type = "Buyer", Email = " CASE@EXAMPLE.TEST ", Owner = user };
        context.Add(contact);
        await context.SaveChangesAsync();
        var createdAt = contact.CreatedAt;
        Assert.Equal(DateTimeKind.Utc, createdAt.Kind);
        Assert.Equal("case@example.test", contact.Email);
        Assert.Null(contact.UpdatedAt);
        contact.LastName = "Two";
        contact.CreatedAt = DateTime.UnixEpoch;
        await context.SaveChangesAsync();
        Assert.Equal(createdAt, contact.CreatedAt);
        Assert.NotNull(contact.UpdatedAt);
        context.Remove(contact);
        await context.SaveChangesAsync();
        Assert.Empty(await context.Contacts.ToListAsync());
        Assert.True((await context.Contacts.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            context.Remove(user);
            await context.SaveChangesAsync();
        });
    }
}



