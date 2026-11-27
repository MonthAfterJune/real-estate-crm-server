using System.Text.Json;
using CRM.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CRM.Tests;

public sealed partial class AuthenticationTests
{
    private async Task<(int Agent, int Seller, int Buyer, int Property, int Opportunity)> Part02Fixture()
    {
        var agent = (await Register()).Json.GetProperty("id").GetInt32();
        var seller = await WorkflowContact("part02-seller@example.com", "Seller");
        var property = await WorkflowDraft(seller);
        Assert.Equal(200, (await WorkflowPublish(property, seller, agent)).Status);
        var buyer = await WorkflowContact("part02-buyer@example.com");
        var opened = await Send("POST", "/api/Opportunities", new { buyerContactId = buyer, propertyId = property }, await Csrf());
        Assert.Equal(201, opened.Status);
        return (agent, seller, buyer, property, opened.Json.GetProperty("id").GetInt32());
    }

    private async Task<TestResponse> Part02Move(int id, int buyer, int property, int agent, int stage, decimal? offer = 2400000) =>
        await Send("PUT", $"/api/Opportunities/{id}", new { buyerContactId = buyer, propertyId = property, agentId = agent, stageId = stage, offerAmount = offer }, await Csrf());

    [Fact]
    public async Task Part02_direction_offer_and_same_stage_edits_keep_exact_history()
    {
        var f = await Part02Fixture();
        Assert.Equal(400, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 4, null)).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 1)).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 4)).Status);
        Assert.Equal(400, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 1)).Status);
        var back = await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 3);
        Assert.Equal(200, back.Status);
        Assert.Equal("Open", back.Json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, back.Json.GetProperty("finalPrice").ValueKind);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 3, 2450000)).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Activities.Where(a => a.Type == "Stage Change").OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].FromStageId); Assert.Equal(4, rows[0].ToStageId);
        Assert.Equal(4, rows[1].FromStageId); Assert.Equal(3, rows[1].ToStageId);
        var details = JsonDocument.Parse(rows[0].Details!).RootElement;
        Assert.Equal("INQUIRY", details.GetProperty("previousStageCode").GetString());
        Assert.Equal("NEGOTIATION", details.GetProperty("stageCode").GetString());
        Assert.Equal("Open", details.GetProperty("previousStatus").GetString());
        Assert.Equal("Available", details.GetProperty("propertyStatus").GetString());
        Assert.All(rows, row => { Assert.True(row.IsSystemGenerated); Assert.NotNull(row.CompletedAt); Assert.Null(row.ScheduledAt); Assert.Equal(f.Agent, row.CreatedById); Assert.Equal(f.Agent, row.UserId); });
    }

    [Fact]
    public async Task Part02_direct_closing_is_exclusive_and_back_steps_retain_then_release()
    {
        var f = await Part02Fixture(); var otherBuyer = await WorkflowContact("part02-other@example.com");
        var other = await Send("POST", "/api/Opportunities", new { buyerContactId = otherBuyer, propertyId = f.Property }, await Csrf());
        var otherId = other.Json.GetProperty("id").GetInt32();
        var closing = await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6);
        Assert.Equal(200, closing.Status); Assert.Equal("Reserved", closing.Json.GetProperty("property").GetProperty("status").GetString());
        Assert.Equal(409, (await Part02Move(otherId, otherBuyer, f.Property, f.Agent, 5)).Status);
        Assert.Equal(400, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 4)).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 5)).Status);
        Assert.Equal("Reserved", (await Send("GET", $"/api/Properties/{f.Property}")).Json.GetProperty("status").GetString());
        Assert.Equal(400, (await WorkflowPublish(f.Property, f.Seller, f.Agent)).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 4)).Status);
        Assert.Equal("Available", (await Send("GET", $"/api/Properties/{f.Property}")).Json.GetProperty("status").GetString());
        Assert.Equal(200, (await Part02Move(otherId, otherBuyer, f.Property, f.Agent, 5)).Status);
        await using var scope = _app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.Opportunities.Where(o => o.StageId >= 5 && o.Status == "Open").ToListAsync());
        Assert.Equal(4, await db.Activities.CountAsync(a => a.Type == "Stage Change"));
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Withdrawn")]
    [InlineData("Sold")]
    public async Task Part02_blocked_listing_states_reject_progress_without_history(string status)
    {
        var f = await Part02Fixture();
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Properties.Where(p => p.Id == f.Property).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status));
        Assert.Equal(400, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 2)).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, (await db.Opportunities.SingleAsync()).StageId);
        Assert.Empty(await db.Activities.Where(a => a.Type == "Stage Change").ToListAsync());
        Assert.Equal(status, (await db.Properties.SingleAsync()).Status);
    }

    [Fact]
    public async Task Part02_history_failure_rolls_back_stage_and_reserved_property()
    {
        var f = await Part02Fixture();
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_stage_history BEFORE INSERT ON "Activities"
                WHEN NEW."Type" = 'Stage Change' BEGIN SELECT RAISE(ABORT, 'fixture stage history failure'); END;
                """);
        Assert.Equal(500, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, (await db.Opportunities.SingleAsync()).StageId);
        Assert.Equal("Available", (await db.Properties.SingleAsync()).Status);
        Assert.Empty(await db.Activities.Where(a => a.Type == "Stage Change").ToListAsync());
    }

    [Fact]
    public async Task Part02_exclusive_relink_and_corrupt_reservation_cannot_transfer_or_release()
    {
        var f = await Part02Fixture(); var seller = await WorkflowContact("second-seller@example.com", "Seller");
        var second = await WorkflowDraft(seller); Assert.Equal(200, (await WorkflowPublish(second, seller, f.Agent)).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 5)).Status);
        Assert.Equal(400, (await Part02Move(f.Opportunity, f.Buyer, second, f.Agent, 5)).Status);
        Assert.Equal(400, (await Part02Move(f.Opportunity, f.Buyer, second, f.Agent, 4)).Status);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Properties.Where(p => p.Id == f.Property).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Available"));
        Assert.Equal(409, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 4)).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(f.Property, (await db.Opportunities.SingleAsync()).PropertyId); Assert.Equal(5, (await db.Opportunities.SingleAsync()).StageId);
        Assert.Single(await db.Activities.Where(a => a.Type == "Stage Change").ToListAsync());
    }

    [Fact]
    public async Task Part02_assigned_agent_logs_private_buyer_viewing_and_reads_immutable_history()
    {
        var f = await Part02Fixture();
        var second = await Send("POST", "/api/auth/register", Account(email: "part02-assigned@example.com"), await Csrf());
        var secondId = second.Json.GetProperty("id").GetInt32();
        await SignInWorkflowManager();
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, secondId, 1, null)).Status);
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "part02-assigned@example.com", password = Password }, await Csrf())).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Contacts/{f.Buyer}")).Status);
        var viewing = await Send("POST", "/api/Activities", new { type = "Viewing", subject = "Private buyer viewing", contactId = f.Buyer, opportunityId = f.Opportunity, propertyId = f.Property, userId = secondId, completedAt = DateTime.UtcNow }, await Csrf());
        Assert.Equal(201, viewing.Status);
        Assert.Equal(1, (await Send("GET", $"/api/Opportunities/{f.Opportunity}")).Json.GetProperty("stageId").GetInt32());
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, secondId, 2, null)).Status);
        var activities = (await Send("GET", "/api/Activities")).Json.EnumerateArray().ToList();
        var generated = Assert.Single(activities.Where(a => a.GetProperty("type").GetString() == "Stage Change"));
        var id = generated.GetProperty("id").GetInt32();
        Assert.Equal(200, (await Send("GET", $"/api/Activities/{id}")).Status);
        Assert.Equal(403, (await Send("DELETE", $"/api/Activities/{id}", csrf: await Csrf())).Status);
        Assert.Equal(403, (await Send("PUT", $"/api/Activities/{id}", new { type = "Call", subject = "Tamper", contactId = f.Buyer, opportunityId = f.Opportunity, propertyId = f.Property, userId = secondId, completedAt = DateTime.UtcNow }, await Csrf())).Status);
        await SignInWorkflowManager();
        Assert.Equal(403, (await Send("DELETE", $"/api/Activities/{id}", csrf: await Csrf())).Status);
    }

    [Fact]
    public async Task Part02_renamed_stages_preserve_code_based_reservation_behavior()
    {
        var f = await Part02Fixture();
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PipelineStages.Where(s => s.Code == "CLOSING")
                .ExecuteUpdateAsync(s => s.SetProperty(stage => stage.Name, "Signing appointment"));
        var moved = await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6);
        Assert.Equal(200, moved.Status);
        Assert.Equal("Signing appointment", moved.Json.GetProperty("stage").GetProperty("name").GetString());
        Assert.Equal("Reserved", moved.Json.GetProperty("property").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Part02_listing_delete_rechecks_retention_and_preserves_live_inquiry_target()
    {
        var f = await Part02Fixture();
        Assert.Equal(400, (await Send("DELETE", $"/api/Properties/{f.Property}", csrf: await Csrf())).Status);
        Assert.Equal(200, (await Send("GET", $"/api/Properties/{f.Property}")).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 5)).Status);
        Assert.Equal(400, (await Send("DELETE", $"/api/Properties/{f.Property}", csrf: await Csrf())).Status);
        Assert.Equal("Reserved", (await Send("GET", $"/api/Properties/{f.Property}")).Json.GetProperty("status").GetString());
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Properties.IgnoreQueryFilters().SingleAsync()).IsDeleted);
    }
}
