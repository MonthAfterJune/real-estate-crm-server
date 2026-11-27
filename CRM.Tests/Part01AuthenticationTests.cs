using System.Text.Json;
using CRM.Data;
using CRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CRM.Tests;

public sealed partial class AuthenticationTests
{
    private async Task<int> SignInWorkflowManager()
    {
        int id;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var role = await db.Roles.SingleOrDefaultAsync(r => r.Name == "manager");
            if (role is null) { role = new Role { Name = "manager" }; db.Roles.Add(role); await db.SaveChangesAsync(); }
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByEmailAsync("workflow-manager@example.com");
            if (user is null)
            {
                user = new User { FullName = "Workflow Manager", Email = "workflow-manager@example.com", UserName = "workflow-manager@example.com", RoleId = role.Id };
                Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            }
            id = user.Id;
        }
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "workflow-manager@example.com", password = Password }, await Csrf())).Status);
        return id;
    }

    private async Task<int> WorkflowContact(string email, string type = "Buyer", int? ownerId = null, string? phone = null)
    {
        var result = await Send("POST", "/api/Contacts", new { firstName = "Workflow", lastName = type, email, phone, type, source = "Website", ownerId }, await Csrf());
        Assert.Equal(201, result.Status);
        return result.Json.GetProperty("id").GetInt32();
    }

    private async Task<int> WorkflowDraft(int sellerId, int? agentId = null)
    {
        var result = await Send("POST", "/api/Properties", new { address = "Part 01 listing", city = "Makati", propertyType = "Condominium", sellerContactId = sellerId, listingAgentId = agentId }, await Csrf());
        Assert.Equal(201, result.Status);
        Assert.Equal("Draft", result.Json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("price").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("bedrooms").ValueKind);
        return result.Json.GetProperty("id").GetInt32();
    }

    private Task<TestResponse> WorkflowPublish(int propertyId, int sellerId, int agentId, decimal? price = 2500000) =>
        WorkflowPublishCore(propertyId, sellerId, agentId, price);
    private async Task<TestResponse> WorkflowPublishCore(int propertyId, int sellerId, int agentId, decimal? price)
    {
        return await Send("PUT", $"/api/Properties/{propertyId}", new { address = "Part 01 listing", city = "Makati", propertyType = "Condominium", sellerContactId = sellerId, listingAgentId = agentId, price, bedrooms = (int?)null, status = "Available" }, await Csrf());
    }

    [Fact]
    public async Task Part01_happy_path_persists_contact_draft_publication_qualification_and_inquiry()
    {
        var account = await Register(); var agentId = account.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("seller@workflow.example", "Seller");
        Assert.Equal(agentId, (await Send("GET", $"/api/Contacts/{seller}")).Json.GetProperty("ownerId").GetInt32());
        var property = await WorkflowDraft(seller);
        Assert.Equal("Lead", (await Send("GET", $"/api/Contacts/{seller}")).Json.GetProperty("status").GetString());
        Assert.Equal(400, (await WorkflowPublish(property, seller, agentId, null)).Status);
        Assert.Equal(400, (await WorkflowPublish(property, seller, agentId, 0)).Status);
        Assert.Equal("Draft", (await Send("GET", $"/api/Properties/{property}")).Json.GetProperty("status").GetString());
        Assert.Equal(200, (await WorkflowPublish(property, seller, agentId)).Status);
        Assert.Equal("Active", (await Send("GET", $"/api/Contacts/{seller}")).Json.GetProperty("status").GetString());
        var buyer = await WorkflowContact("buyer@workflow.example");
        foreach (var scheduled in new[] { false, true })
        {
            var activity = await Send("POST", "/api/Activities", new { type = scheduled ? "Task" : "Call", subject = "Qualify buyer", contactId = buyer, userId = agentId,
                completedAt = scheduled ? (DateTime?)null : DateTime.UtcNow, scheduledAt = scheduled ? DateTime.UtcNow.AddDays(1) : (DateTime?)null }, await Csrf());
            Assert.Equal(201, activity.Status);
            Assert.Equal(JsonValueKind.Null, activity.Json.GetProperty("opportunityId").ValueKind);
            Assert.Equal(agentId, activity.Json.GetProperty("createdById").GetInt32());
        }
        Assert.Equal("Lead", (await Send("GET", $"/api/Contacts/{buyer}")).Json.GetProperty("status").GetString());
        var opened = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf());
        Assert.Equal(201, opened.Status);
        Assert.Equal("Open", opened.Json.GetProperty("status").GetString());
        Assert.Equal("INQUIRY", opened.Json.GetProperty("stage").GetProperty("code").GetString());
        Assert.Equal(agentId, opened.Json.GetProperty("agentId").GetInt32());
        Assert.Equal(0.03m, opened.Json.GetProperty("commissionRate").GetDecimal());
        Assert.Equal(JsonValueKind.Null, opened.Json.GetProperty("offerAmount").ValueKind);
        Assert.Equal("Active", (await Send("GET", $"/api/Contacts/{buyer}")).Json.GetProperty("status").GetString());
        Assert.Equal(409, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf())).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.Opportunities.ToListAsync());
        Assert.Equal(3, await db.Activities.CountAsync()); // Price publication history plus call/task.
        Assert.Equal("Available", (await db.Properties.SingleAsync()).Status);
    }

    [Fact]
    public async Task Part01_normalizes_channels_and_only_exposes_visible_duplicate_id()
    {
        var first = await Register();
        var created = await Send("POST", "/api/Contacts", new { firstName = "Private", lastName = "Person", email = "  PERSON@EXAMPLE.COM ", phone = "+63 (917) 123-4567", type = "Both", source = "Website" }, await Csrf());
        Assert.Equal(201, created.Status);
        Assert.Equal("person@example.com", created.Json.GetProperty("email").GetString());
        Assert.Equal("+639171234567", created.Json.GetProperty("phone").GetString());
        var id = created.Json.GetProperty("id").GetInt32();
        var duplicate = await Send("POST", "/api/Contacts", new { firstName = "Other", lastName = "Name", phone = "+63.917.123.4567", type = "Buyer", source = "Phone" }, await Csrf());
        Assert.Equal(400, duplicate.Status); Assert.Equal(id, duplicate.Json.GetProperty("existingContactId").GetInt32());
        var second = await Send("POST", "/api/auth/register", Account(email: "second-agent@example.com"), await Csrf());
        Assert.Equal(201, second.Status);
        var hidden = await Send("POST", "/api/Contacts", new { firstName = "Other", lastName = "Person", email = "PERSON@example.com", type = "Buyer", source = "Website" }, await Csrf());
        Assert.Equal(400, hidden.Status); Assert.False(hidden.Json.TryGetProperty("existingContactId", out _));
        Assert.DoesNotContain("Private", hidden.Body);
        Assert.Equal(404, (await Send("GET", $"/api/Contacts/{id}")).Status);
        Assert.Empty((await Send("GET", "/api/Contacts")).Json.EnumerateArray());
        Assert.Equal(400, (await Send("POST", "/api/Activities", new { type = "Call", subject = "Hidden contact", contactId = id, userId = second.Json.GetProperty("id").GetInt32(), completedAt = DateTime.UtcNow }, await Csrf())).Status);
    }

    [Fact]
    public async Task Part01_defaults_are_not_assignment_permissions_and_manager_must_select_agent()
    {
        var agent = await Register(); var agentId = agent.Json.GetProperty("id").GetInt32();
        int otherId;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var other = new User { FullName = "Other Agent", Email = "other@workflow.example", UserName = "other@workflow.example", RoleId = 1 };
            Assert.True((await users.CreateAsync(other)).Succeeded); otherId = other.Id;
        }
        Assert.Equal(403, (await Send("POST", "/api/Contacts", new { firstName = "Not", lastName = "Mine", type = "Buyer", email = "not-mine@example.com", source = "Website", ownerId = otherId }, await Csrf())).Status);
        var contact = await WorkflowContact("mine@example.com");
        Assert.Equal(403, (await Send("PUT", $"/api/Contacts/{contact}", new { firstName = "Workflow", lastName = "Buyer", type = "Buyer", email = "mine@example.com", source = "Website", ownerId = otherId }, await Csrf())).Status);
        var managerId = await SignInWorkflowManager();
        Assert.Equal(400, (await Send("POST", "/api/Contacts", new { firstName = "Need", lastName = "Owner", type = "Buyer", email = "need-owner@example.com", source = "Website" }, await Csrf())).Status);
        var changed = await Send("PUT", $"/api/Contacts/{contact}", new { firstName = "Workflow", lastName = "Buyer", type = "Buyer", email = "mine@example.com", source = "Website", ownerId = otherId }, await Csrf());
        Assert.Equal(200, changed.Status);
        await using var verify = _app.Services.CreateAsyncScope();
        var history = await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().Activities.SingleAsync();
        Assert.Equal(managerId, history.CreatedById); Assert.Equal(otherId, history.UserId); Assert.True(history.IsSystemGenerated);
    }

    [Fact]
    public async Task Part01_rejects_type_narrowing_but_allows_reusing_seller_as_both()
    {
        var account = await Register(); var actor = account.Json.GetProperty("id").GetInt32();
        var contact = await WorkflowContact("both@example.com", "Seller");
        await WorkflowDraft(contact);
        async Task<TestResponse> Change(string type) => await Send("PUT", $"/api/Contacts/{contact}", new { firstName = "Workflow", lastName = "Seller", email = "both@example.com", type, source = "Website", ownerId = actor }, await Csrf());
        Assert.Equal(400, (await Change("Buyer")).Status);
        Assert.Equal(200, (await Change("Both")).Status);
        var seller = await WorkflowContact("another-seller@example.com", "Seller");
        var property = await WorkflowDraft(seller); Assert.Equal(200, (await WorkflowPublish(property, seller, actor)).Status);
        Assert.Equal(201, (await Send("POST", "/api/Opportunities", new { buyerContactId = contact, propertyId = property }, await Csrf())).Status);
        Assert.Equal(400, (await Change("Seller")).Status);
    }

    [Fact]
    public async Task Part01_readonly_listings_do_not_leak_contacts_deals_or_notes()
    {
        var first = await Register(); var firstId = first.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("private-seller@example.com", "Seller");
        var property = await WorkflowDraft(seller); Assert.Equal(200, (await WorkflowPublish(property, seller, firstId)).Status);
        var second = await Send("POST", "/api/auth/register", Account(email: "reader@example.com"), await Csrf());
        var secondId = second.Json.GetProperty("id").GetInt32();
        var listing = await Send("GET", $"/api/Properties/{property}");
        Assert.Equal(200, listing.Status); Assert.Equal(JsonValueKind.Null, listing.Json.GetProperty("sellerContact").ValueKind);
        Assert.Equal(404, (await WorkflowPublish(property, seller, secondId)).Status);
        var buyer = await WorkflowContact("reader-buyer@example.com");
        var deal = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf());
        Assert.Equal(201, deal.Status); var dealId = deal.Json.GetProperty("id").GetInt32();
        var note = await Send("POST", "/api/Notes", new { authorId = secondId, entityType = "Opportunity", entityId = dealId, body = "Private buyer details" }, await Csrf());
        Assert.Equal(201, note.Status);
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "taylor@example.com", password = Password }, await Csrf())).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Opportunities/{dealId}")).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Notes/{note.Json.GetProperty("id").GetInt32()}")).Status);
        Assert.Empty((await Send("GET", "/api/Notes")).Json.EnumerateArray());
    }

    [Fact]
    public async Task Part01_rejects_non_draft_and_non_inquiry_creation_and_snapshots_rate()
    {
        var account = await Register(); var actor = account.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("seller@example.com", "Seller");
        Assert.Equal(400, (await Send("POST", "/api/Properties", new { address = "Bypass", city = "Makati", propertyType = "House", sellerContactId = seller, price = 100, status = "Available" }, await Csrf())).Status);
        var property = await WorkflowDraft(seller); var buyer = await WorkflowContact("buyer@example.com");
        Assert.Equal(400, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf())).Status);
        Assert.Equal(200, (await WorkflowPublish(property, seller, actor)).Status);
        Assert.Equal(400, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property, stageId = 3, offerAmount = 10 }, await Csrf())).Status);
        var opened = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf()); Assert.Equal(201, opened.Status);
        _app.Services.GetRequiredService<IConfiguration>()["DefaultCommissionRate"] = "0.08";
        var buyer2 = await WorkflowContact("buyer2@example.com");
        var second = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer2, propertyId = property }, await Csrf()); Assert.Equal(201, second.Status);
        Assert.Equal(0.08m, second.Json.GetProperty("commissionRate").GetDecimal());
        Assert.Equal(0.03m, (await Send("GET", $"/api/Opportunities/{opened.Json.GetProperty("id").GetInt32()}")).Json.GetProperty("commissionRate").GetDecimal());
    }

    [Fact]
    public async Task Part01_failed_status_write_rolls_back_publication_price_and_history()
    {
        var account = await Register(); var actor = account.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("rollback@example.com", "Seller"); var property = await WorkflowDraft(seller);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_contact_activation BEFORE UPDATE OF "Status" ON "Contacts"
                WHEN NEW."Status" = 'Active' BEGIN SELECT RAISE(ABORT, 'fixture status failure'); END;
                """);
        Assert.Equal(500, (await WorkflowPublish(property, seller, actor)).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.Properties.SingleAsync(); Assert.Equal("Draft", persisted.Status); Assert.Null(persisted.Price);
        Assert.Equal("Lead", (await db.Contacts.SingleAsync()).Status); Assert.Empty(await db.Activities.ToListAsync());
    }

    [Fact]
    public async Task Part01_preserves_manual_inactive_and_rejects_missing_rate_without_partial_creation()
    {
        var account = await Register(); var actor = account.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("seller@example.com", "Seller"); var property = await WorkflowDraft(seller); Assert.Equal(200, (await WorkflowPublish(property, seller, actor)).Status);
        var buyer = await WorkflowContact("inactive@example.com");
        Assert.Equal(200, (await Send("PUT", $"/api/Contacts/{buyer}", new { firstName = "Workflow", lastName = "Buyer", email = "inactive@example.com", type = "Buyer", source = "Website", ownerId = actor, isManuallyInactive = true }, await Csrf())).Status);
        _app.Services.GetRequiredService<IConfiguration>()["DefaultCommissionRate"] = null;
        Assert.Equal(503, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf())).Status);
        _app.Services.GetRequiredService<IConfiguration>()["DefaultCommissionRate"] = "0.03";
        Assert.Equal(201, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf())).Status);
        Assert.Equal("Inactive", (await Send("GET", $"/api/Contacts/{buyer}")).Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Part01_first_inquiry_can_compete_on_reserved_property_but_closed_retry_requires_available()
    {
        var account = await Register(); var actor = account.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("reserved-seller@example.com", "Seller"); var property = await WorkflowDraft(seller);
        Assert.Equal(200, (await WorkflowPublish(property, seller, actor)).Status);
        var holder = await WorkflowContact("reservation-holder@example.com");
        var buyer = await WorkflowContact("competing-buyer@example.com");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.Properties.SingleAsync()).Status = "Reserved";
            db.Opportunities.Add(new Opportunity { BuyerContactId = holder, PropertyId = property, AgentId = actor, StageId = 5, Status = "Open", OfferAmount = 2400000, CommissionRate = 0.03m });
            await db.SaveChangesAsync();
        }
        var first = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf());
        Assert.Equal(201, first.Status);
        Assert.Equal("Reserved", (await Send("GET", $"/api/Properties/{property}")).Json.GetProperty("status").GetString());
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var attempt = await db.Opportunities.SingleAsync(o => o.BuyerContactId == buyer);
            attempt.Status = "Lost"; attempt.LostReason = "Changed mind"; attempt.ClosedAt = DateTime.UtcNow;
            (await db.Contacts.SingleAsync(c => c.Id == buyer)).Status = "Lead";
            await db.SaveChangesAsync();
        }
        Assert.Equal(400, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf())).Status);
        Assert.Equal("Lead", (await Send("GET", $"/api/Contacts/{buyer}")).Json.GetProperty("status").GetString());
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reservation = await db.Opportunities.SingleAsync(o => o.BuyerContactId == holder);
            reservation.Status = "Lost"; reservation.LostReason = "Changed mind"; reservation.ClosedAt = DateTime.UtcNow;
            (await db.Properties.SingleAsync()).Status = "Available";
            await db.SaveChangesAsync();
        }
        Assert.Equal(201, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf())).Status);
        await using var verify = _app.Services.CreateAsyncScope();
        Assert.Equal(2, await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().Opportunities.CountAsync(o => o.BuyerContactId == buyer));
    }

    [Fact]
    public async Task Part01_edit_can_retain_private_seller_reference_and_manager_seller_change_refreshes_both_contacts()
    {
        var first = await Register(); var firstId = first.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("retained-seller@example.com", "Seller");
        var replacement = await WorkflowContact("replacement-seller@example.com", "Seller");
        var property = await WorkflowDraft(seller); Assert.Equal(200, (await WorkflowPublish(property, seller, firstId)).Status);
        var second = await Send("POST", "/api/auth/register", Account(email: "assigned-listing@example.com"), await Csrf()); var secondId = second.Json.GetProperty("id").GetInt32();
        await SignInWorkflowManager();
        Assert.Equal(200, (await WorkflowPublish(property, seller, secondId)).Status);
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "assigned-listing@example.com", password = Password }, await Csrf())).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Contacts/{seller}")).Status);
        var retained = await WorkflowPublish(property, seller, secondId);
        Assert.Equal(200, retained.Status); Assert.Equal(JsonValueKind.Null, retained.Json.GetProperty("sellerContact").ValueKind);
        Assert.Equal(400, (await WorkflowPublish(property, replacement, secondId)).Status);
        await SignInWorkflowManager();
        Assert.Equal(200, (await WorkflowPublish(property, replacement, secondId)).Status);
        Assert.Equal("Lead", (await Send("GET", $"/api/Contacts/{seller}")).Json.GetProperty("status").GetString());
        Assert.Equal("Active", (await Send("GET", $"/api/Contacts/{replacement}")).Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Part01_failed_buyer_status_write_rolls_back_opportunity_creation()
    {
        var account = await Register(); var actor = account.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("seller@example.com", "Seller"); var property = await WorkflowDraft(seller);
        Assert.Equal(200, (await WorkflowPublish(property, seller, actor)).Status);
        var buyer = await WorkflowContact("rollback-buyer@example.com");
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_buyer_activation BEFORE UPDATE OF "Status" ON "Contacts"
                WHEN NEW."Email" = 'rollback-buyer@example.com' AND NEW."Status" = 'Active' BEGIN SELECT RAISE(ABORT, 'fixture status failure'); END;
                """);
        Assert.Equal(500, (await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf())).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.Opportunities.ToListAsync());
        Assert.Equal("Lead", (await db.Contacts.SingleAsync(c => c.Id == buyer)).Status);
        Assert.Single(await db.Activities.ToListAsync()); // Earlier successful publication history is retained.
    }

    [Fact]
    public async Task Assigned_opportunity_agent_can_retain_private_buyer_but_cannot_select_another_hidden_contact()
    {
        var first = await Register(); var firstId = first.Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("seller-retained-buyer@example.com", "Seller");
        var property = await WorkflowDraft(seller); Assert.Equal(200, (await WorkflowPublish(property, seller, firstId)).Status);
        var buyer = await WorkflowContact("retained-buyer@example.com");
        var otherPrivateBuyer = await WorkflowContact("hidden-replacement@example.com");
        var opened = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf());
        Assert.Equal(201, opened.Status); var opportunity = opened.Json.GetProperty("id").GetInt32();
        var second = await Send("POST", "/api/auth/register", Account(email: "assigned-opportunity@example.com"), await Csrf());
        Assert.Equal(201, second.Status); var secondId = second.Json.GetProperty("id").GetInt32();
        await SignInWorkflowManager();
        var assigned = await Send("PUT", $"/api/Opportunities/{opportunity}", new { buyerContactId = buyer, propertyId = property, agentId = secondId, stageId = 1 }, await Csrf());
        Assert.Equal(200, assigned.Status);
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "assigned-opportunity@example.com", password = Password }, await Csrf())).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Contacts/{buyer}")).Status);
        var visible = await Send("GET", $"/api/Opportunities/{opportunity}");
        Assert.Equal(200, visible.Status); Assert.Equal(JsonValueKind.Null, visible.Json.GetProperty("buyerContact").ValueKind);
        var retained = await Send("PUT", $"/api/Opportunities/{opportunity}", new { buyerContactId = buyer, propertyId = property, agentId = secondId, stageId = 2 }, await Csrf());
        Assert.Equal(200, retained.Status);
        Assert.Equal(2, retained.Json.GetProperty("stageId").GetInt32());
        Assert.Equal(buyer, retained.Json.GetProperty("buyerContactId").GetInt32());
        Assert.Equal(JsonValueKind.Null, retained.Json.GetProperty("buyerContact").ValueKind);
        var replaced = await Send("PUT", $"/api/Opportunities/{opportunity}", new { buyerContactId = otherPrivateBuyer, propertyId = property, agentId = secondId, stageId = 2 }, await Csrf());
        Assert.Equal(400, replaced.Status);
        Assert.True(replaced.Json.GetProperty("errors").TryGetProperty("BuyerContactId", out _));
        Assert.Equal(buyer, (await Send("GET", $"/api/Opportunities/{opportunity}")).Json.GetProperty("buyerContactId").GetInt32());
    }
}
