using System.Text.Json;
using CRM.Data;
using CRM.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CRM.Tests;

public sealed partial class AuthenticationTests
{
    private async Task<TestResponse> Part03Won(int id, decimal price) =>
        await Send("POST", $"/api/Opportunities/{id}/won", new { finalPrice = price }, await Csrf());
    private async Task<TestResponse> Part03Lost(int id, string reason) =>
        await Send("POST", $"/api/Opportunities/{id}/lost", new { lostReason = reason }, await Csrf());
    private async Task<TestResponse> Part03Rate(int id, decimal rate) =>
        await Send("PUT", $"/api/Opportunities/{id}/commission-rate", new { commissionRate = rate }, await Csrf());

    [Fact]
    public async Task Part03_won_sells_and_closes_all_competitors_with_history_and_contact_statuses()
    {
        var f = await Part02Fixture(); var competitorBuyer = await WorkflowContact("competing-part03@example.com");
        var competitor = await Send("POST", "/api/Opportunities", new { buyerContactId = competitorBuyer, propertyId = f.Property }, await Csrf());
        var competitorId = competitor.Json.GetProperty("id").GetInt32();
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        var won = await Part03Won(f.Opportunity, 2500000);
        Assert.Equal(200, won.Status); Assert.Equal("Won", won.Json.GetProperty("status").GetString());
        Assert.Equal(75000, won.Json.GetProperty("commissionAmount").GetDecimal());
        Assert.Equal("Sold", won.Json.GetProperty("property").GetProperty("status").GetString());
        Assert.Equal(6, won.Json.GetProperty("stageId").GetInt32());
        Assert.Equal(JsonValueKind.Null, won.Json.GetProperty("lostReason").ValueKind);
        var lost = await Send("GET", $"/api/Opportunities/{competitorId}");
        Assert.Equal("Lost", lost.Json.GetProperty("status").GetString());
        Assert.Equal("Property sold", lost.Json.GetProperty("lostReason").GetString());
        Assert.Equal(1, lost.Json.GetProperty("stageId").GetInt32());
        Assert.Equal(JsonValueKind.Null, lost.Json.GetProperty("finalPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, lost.Json.GetProperty("commissionAmount").ValueKind);
        Assert.Equal(won.Json.GetProperty("closedAt").GetString(), lost.Json.GetProperty("closedAt").GetString());
        Assert.Equal("Client", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
        Assert.Equal("Client", (await Send("GET", $"/api/Contacts/{f.Seller}")).Json.GetProperty("status").GetString());
        Assert.Equal("Lead", (await Send("GET", $"/api/Contacts/{competitorBuyer}")).Json.GetProperty("status").GetString());
        Assert.Equal(409, (await Part03Won(f.Opportunity, 1)).Status);
        Assert.Equal(409, (await Part03Lost(competitorId, "Other")).Status);
        Assert.Equal(400, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 5)).Status);
        Assert.Equal(400, (await WorkflowPublish(f.Property, f.Seller, f.Agent)).Status);
        await using var scope = _app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var outcomeHistory = await db.Activities.Where(a => a.Subject == "Opportunity won" || a.Subject.StartsWith("Opportunity automatically lost")).ToListAsync();
        Assert.Equal(2, outcomeHistory.Count);
        Assert.All(outcomeHistory, a => { Assert.Equal("System", a.Type); Assert.True(a.IsSystemGenerated); Assert.Null(a.FromStageId); Assert.Null(a.ToStageId); Assert.Null(a.ScheduledAt); Assert.Equal(f.Agent, a.CreatedById); Assert.NotNull(a.CompletedAt); });
        var cascade = JsonDocument.Parse(outcomeHistory.Single(a => a.OpportunityId == competitorId).Details!).RootElement;
        Assert.True(cascade.GetProperty("automatic").GetBoolean()); Assert.Equal("Open", cascade.GetProperty("previousStatus").GetString());
        Assert.Equal("Sold", cascade.GetProperty("propertyStatus").GetString());
        Assert.Single(await db.Activities.Where(a => a.Type == "Stage Change").ToListAsync());
    }

    [Theory]
    [InlineData("Price")]
    [InlineData("Bought elsewhere")]
    [InlineData("Financing")]
    [InlineData("Changed mind")]
    [InlineData("Property sold")]
    [InlineData("No response")]
    [InlineData("Other")]
    public async Task Part03_lost_reason_closes_inquiry_without_changing_stage_or_available_listing(string reason)
    {
        var f = await Part02Fixture(); var lost = await Part03Lost(f.Opportunity, reason);
        Assert.Equal(200, lost.Status); Assert.Equal("Lost", lost.Json.GetProperty("status").GetString());
        Assert.Equal(reason, lost.Json.GetProperty("lostReason").GetString()); Assert.Equal(1, lost.Json.GetProperty("stageId").GetInt32());
        Assert.Equal("Available", lost.Json.GetProperty("property").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, lost.Json.GetProperty("commissionAmount").ValueKind);
        Assert.Equal("Lead", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Withdrawn")]
    [InlineData("Sold")]
    public async Task Part03_loss_of_exclusive_deal_on_blocked_listing_preserves_listing(string status)
    {
        var f = await Part02Fixture(); Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Properties.Where(p => p.Id == f.Property).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, status));
        var result = await Part03Lost(f.Opportunity, "Other"); Assert.Equal(200, result.Status);
        Assert.Equal(status, result.Json.GetProperty("property").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Part03_loss_releases_only_sole_exclusive_holder_and_preserves_manual_inactive()
    {
        var f = await Part02Fixture();
        Assert.Equal(200, (await Send("PUT", $"/api/Contacts/{f.Buyer}", new { firstName = "Workflow", lastName = "Buyer", email = "part02-buyer@example.com", type = "Buyer", source = "Website", ownerId = f.Agent, isManuallyInactive = true }, await Csrf())).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 5)).Status);
        var lost = await Part03Lost(f.Opportunity, "Changed mind"); Assert.Equal(200, lost.Status);
        Assert.Equal("Available", lost.Json.GetProperty("property").GetProperty("status").GetString());
        Assert.Equal("Inactive", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
        var buyer = await WorkflowContact("inconsistent-part03@example.com");
        var opened = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = f.Property }, await Csrf());
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Properties.Where(p => p.Id == f.Property).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, "Reserved"));
        var nonholderLoss = await Part03Lost(opened.Json.GetProperty("id").GetInt32(), "Other");
        Assert.Equal(200, nonholderLoss.Status); Assert.Equal("Reserved", nonholderLoss.Json.GetProperty("property").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Part03_validates_strict_commands_and_keeps_open_state_on_rejection()
    {
        var f = await Part02Fixture();
        Assert.Equal(400, (await Part03Won(f.Opportunity, 100)).Status); // Not Closing.
        foreach (var price in new[] { 0m, -1m, 1.001m }) Assert.Equal(400, (await Part03Won(f.Opportunity, price)).Status);
        Assert.Equal(400, (await Send("POST", $"/api/Opportunities/{f.Opportunity}/won", new { }, await Csrf())).Status);
        Assert.Equal(400, (await Send("POST", $"/api/Opportunities/{f.Opportunity}/won", new { finalPrice = 100, status = "Won" }, await Csrf())).Status);
        Assert.Equal(400, (await Part03Lost(f.Opportunity, "Unsupported")).Status);
        Assert.Equal(400, (await Part03Lost(f.Opportunity, "")).Status);
        Assert.Equal("Open", (await Send("GET", $"/api/Opportunities/{f.Opportunity}")).Json.GetProperty("status").GetString());
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Activities.Where(a => a.Subject == "Opportunity won" || a.Subject == "Opportunity lost").ToListAsync());
    }

    [Fact]
    public async Task Part03_manager_rate_validates_precision_noops_and_won_half_up_commission()
    {
        var f = await Part02Fixture(); Assert.Equal(403, (await Part03Rate(f.Opportunity, 0.5m)).Status);
        await SignInWorkflowManager();
        foreach (var rate in new[] { -0.01m, 1.01m, 0.0000001m }) Assert.Equal(400, (await Part03Rate(f.Opportunity, rate)).Status);
        Assert.Equal(400, (await Send("PUT", $"/api/Opportunities/{f.Opportunity}/commission-rate", new { }, await Csrf())).Status);
        Assert.Equal(200, (await Part03Rate(f.Opportunity, 0)).Status);
        Assert.Equal(200, (await Part03Rate(f.Opportunity, 1)).Status);
        Assert.Equal(200, (await Part03Rate(f.Opportunity, 0.5m)).Status);
        Assert.Equal(200, (await Part03Rate(f.Opportunity, 0.5000000m)).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        var won = await Part03Won(f.Opportunity, 0.01m); Assert.Equal(200, won.Status);
        Assert.Equal(0.01m, won.Json.GetProperty("commissionAmount").GetDecimal()); // 0.005 midpoint, not ties-to-even.
        Assert.Equal(409, (await Part03Rate(f.Opportunity, 0.03m)).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Activities.CountAsync(a => a.Subject == "Commission rate changed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Part03_failed_cascade_history_or_contact_write_rolls_back_entire_sale(bool failContact)
    {
        var f = await Part02Fixture(); var competitorBuyer = await WorkflowContact("rollback-cascade@example.com");
        Assert.Equal(201, (await Send("POST", "/api/Opportunities", new { buyerContactId = competitorBuyer, propertyId = f.Property }, await Csrf())).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync(failContact ? """
                CREATE TRIGGER fail_client_status BEFORE UPDATE OF "Status" ON "Contacts"
                WHEN NEW."Status" = 'Client' BEGIN SELECT RAISE(ABORT, 'fixture contact failure'); END;
                """ : """
                CREATE TRIGGER fail_competing_loss BEFORE INSERT ON "Activities"
                WHEN NEW."Subject" LIKE 'Opportunity automatically lost%' BEGIN SELECT RAISE(ABORT, 'fixture cascade history failure'); END;
                """);
        Assert.Equal(500, (await Part03Won(f.Opportunity, 2500000)).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.All(await db.Opportunities.ToListAsync(), o => { Assert.Equal("Open", o.Status); Assert.Null(o.ClosedAt); Assert.Null(o.FinalPrice); });
        Assert.Equal("Reserved", (await db.Properties.SingleAsync()).Status);
        Assert.All(await db.Contacts.ToListAsync(), c => Assert.Equal("Active", c.Status));
        Assert.Empty(await db.Activities.Where(a => a.Subject == "Opportunity won" || a.Subject.StartsWith("Opportunity automatically lost")).ToListAsync());
    }

    [Fact]
    public async Task Part03_assigned_private_buyer_winner_closes_hidden_competitor_without_disclosure()
    {
        var f = await Part02Fixture(); var competitorBuyer = await WorkflowContact("hidden-cascade-buyer@example.com");
        var competitor = await Send("POST", "/api/Opportunities", new { buyerContactId = competitorBuyer, propertyId = f.Property }, await Csrf());
        var competitorId = competitor.Json.GetProperty("id").GetInt32();
        var second = await Send("POST", "/api/auth/register", Account(email: "part03-assigned@example.com"), await Csrf());
        var secondId = second.Json.GetProperty("id").GetInt32();
        await SignInWorkflowManager();
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, secondId, 6)).Status);
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "part03-assigned@example.com", password = Password }, await Csrf())).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Contacts/{f.Buyer}")).Status);
        Assert.Equal(404, (await Part03Lost(competitorId, "Other")).Status);
        var result = await Part03Won(f.Opportunity, 2500000);
        Assert.Equal(200, result.Status); Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("buyerContact").ValueKind);
        Assert.Equal(404, (await Send("GET", $"/api/Opportunities/{competitorId}")).Status);
        var visible = (await Send("GET", "/api/Activities")).Json.EnumerateArray().ToArray();
        Assert.DoesNotContain(visible, a => a.GetProperty("subject").GetString()!.StartsWith("Opportunity automatically lost"));
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Lost", (await db.Opportunities.SingleAsync(o => o.Id == competitorId)).Status);
        var automatic = await db.Activities.SingleAsync(a => a.OpportunityId == competitorId && a.Type == "System");
        Assert.Equal(f.Agent, automatic.UserId); Assert.Equal(secondId, automatic.CreatedById);
        Assert.DoesNotContain("hidden-cascade-buyer", automatic.Details!);
    }

    [Fact]
    public async Task Part03_loss_keeps_buyer_active_for_other_open_and_client_for_prior_won()
    {
        var f = await Part02Fixture(); var otherProperty = await WorkflowDraft(f.Seller);
        Assert.Equal(200, (await WorkflowPublish(otherProperty, f.Seller, f.Agent)).Status);
        var other = await Send("POST", "/api/Opportunities", new { buyerContactId = f.Buyer, propertyId = otherProperty }, await Csrf());
        Assert.Equal(201, other.Status);
        Assert.Equal(200, (await Part03Lost(f.Opportunity, "Other")).Status);
        Assert.Equal("Active", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
        var otherId = other.Json.GetProperty("id").GetInt32();
        Assert.Equal(200, (await Part02Move(otherId, f.Buyer, otherProperty, f.Agent, 6)).Status);
        Assert.Equal(200, (await Part03Won(otherId, 100)).Status);
        var next = await Send("POST", "/api/Opportunities", new { buyerContactId = f.Buyer, propertyId = f.Property }, await Csrf());
        Assert.Equal(201, next.Status);
        Assert.Equal(200, (await Part03Lost(next.Json.GetProperty("id").GetInt32(), "Other")).Status);
        Assert.Equal("Client", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
    }
}
