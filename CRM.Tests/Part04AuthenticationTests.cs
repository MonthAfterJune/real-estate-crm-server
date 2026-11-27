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
    private async Task<TestResponse> Part04Reopen(int id, string reason = "Buyer asked to resume discussions") =>
        await Send("POST", $"/api/Opportunities/{id}/reopen", new { reason }, await Csrf());

    private async Task<int> Part04SignInAdmin()
    {
        int id;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var role = new Role { Name = "admin" }; db.Roles.Add(role); await db.SaveChangesAsync();
            var user = new User { FullName = "Reopen Admin", Email = "reopen-admin@example.com", UserName = "reopen-admin@example.com", RoleId = role.Id };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, Password)).Succeeded);
            id = user.Id;
        }
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "reopen-admin@example.com", password = Password }, await Csrf())).Status);
        return id;
    }

    [Fact]
    public async Task Part04_lost_inquiry_reopens_same_id_preserving_snapshots_and_equal_stage_history()
    {
        var f = await Part02Fixture();
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 1, 2450000)).Status);
        await SignInWorkflowManager(); Assert.Equal(200, (await Part03Rate(f.Opportunity, 0.123456m)).Status);
        Assert.Equal(200, (await Part03Lost(f.Opportunity, "Changed mind")).Status);
        _app.Services.GetRequiredService<IConfiguration>()["DefaultCommissionRate"] = "0.9";
        var reopened = await Part04Reopen(f.Opportunity, "  Financing reviewed and buyer returned.  ");
        Assert.Equal(200, reopened.Status);
        Assert.Equal(f.Opportunity, reopened.Json.GetProperty("id").GetInt32());
        Assert.Equal(f.Buyer, reopened.Json.GetProperty("buyerContactId").GetInt32());
        Assert.Equal(f.Property, reopened.Json.GetProperty("propertyId").GetInt32());
        Assert.Equal(f.Agent, reopened.Json.GetProperty("agentId").GetInt32());
        Assert.Equal("Open", reopened.Json.GetProperty("status").GetString());
        Assert.Equal("INQUIRY", reopened.Json.GetProperty("stage").GetProperty("code").GetString());
        Assert.Equal(2450000, reopened.Json.GetProperty("offerAmount").GetDecimal());
        Assert.Equal(0.123456m, reopened.Json.GetProperty("commissionRate").GetDecimal());
        foreach (var key in new[] { "finalPrice", "lostReason", "closedAt", "commissionAmount" })
            Assert.Equal(JsonValueKind.Null, reopened.Json.GetProperty(key).ValueKind);
        Assert.Equal("Active", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
        Assert.Equal(409, (await Part04Reopen(f.Opportunity)).Status);
        await using var scope = _app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.Opportunities.ToListAsync());
        Assert.Single(await db.Activities.Where(a => a.Subject == "Opportunity lost").ToListAsync());
        var history = await db.Activities.SingleAsync(a => a.Subject == "Opportunity reopened");
        Assert.Equal("Stage Change", history.Type); Assert.Equal(1, history.FromStageId); Assert.Equal(1, history.ToStageId);
        Assert.True(history.IsSystemGenerated); Assert.NotNull(history.CompletedAt); Assert.Null(history.ScheduledAt);
        var details = JsonDocument.Parse(history.Details!).RootElement;
        Assert.Equal("Financing reviewed and buyer returned.", details.GetProperty("reason").GetString());
        Assert.Equal("Lost", details.GetProperty("previousStatus").GetString());
        Assert.Equal("Changed mind", details.GetProperty("previousLostReason").GetString());
        Assert.Equal(0.123456m, details.GetProperty("previousCommissionRate").GetDecimal());
    }

    [Fact]
    public async Task Part04_won_reopening_returns_available_preserves_closed_competitors_and_reuses_half_up_rate()
    {
        var f = await Part02Fixture(); var competitorBuyer = await WorkflowContact("reopen-competitor@example.com");
        var competing = await Send("POST", "/api/Opportunities", new { buyerContactId = competitorBuyer, propertyId = f.Property }, await Csrf());
        var competingId = competing.Json.GetProperty("id").GetInt32();
        await SignInWorkflowManager(); Assert.Equal(200, (await Part03Rate(f.Opportunity, 0.5m)).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        Assert.Equal(200, (await Part03Won(f.Opportunity, 0.01m)).Status);
        var closedCompetitor = (await Send("GET", $"/api/Opportunities/{competingId}")).Json.Clone();
        var reopened = await Part04Reopen(f.Opportunity, "Sale cancelled and listing returned"); Assert.Equal(200, reopened.Status);
        Assert.Equal("Available", reopened.Json.GetProperty("property").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reopened.Json.GetProperty("commissionAmount").ValueKind);
        Assert.Equal("Active", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
        Assert.Equal("Active", (await Send("GET", $"/api/Contacts/{f.Seller}")).Json.GetProperty("status").GetString());
        var retained = (await Send("GET", $"/api/Opportunities/{competingId}")).Json;
        foreach (var key in new[] { "status", "lostReason", "closedAt", "stageId" })
            Assert.Equal(closedCompetitor.GetProperty(key).ToString(), retained.GetProperty(key).ToString());
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var history = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Activities.SingleAsync(a => a.Subject == "Opportunity reopened");
            var detail = JsonDocument.Parse(history.Details!).RootElement;
            Assert.Equal(0.01m, detail.GetProperty("previousCommissionAmount").GetDecimal());
            Assert.Equal(0.01m, detail.GetProperty("previousFinalPrice").GetDecimal());
            Assert.Equal("Sold", detail.GetProperty("previousPropertyStatus").GetString());
            Assert.Equal(6, history.FromStageId); Assert.Equal(1, history.ToStageId);
        }
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        Assert.Equal(0.01m, (await Part03Won(f.Opportunity, 0.01m)).Json.GetProperty("commissionAmount").GetDecimal());
    }

    [Fact]
    public async Task Part04_reopen_requires_privileged_role_and_strict_trimmed_reason()
    {
        var f = await Part02Fixture(); Assert.Equal(200, (await Part03Lost(f.Opportunity, "Other")).Status);
        Assert.Equal(403, (await Part04Reopen(f.Opportunity)).Status);
        var adminId = await Part04SignInAdmin();
        foreach (var body in new object[] { new { }, new { reason = "   " }, new { reason = new string('x', 2001) }, new { reason = "Valid", status = "Open" } })
            Assert.Equal(400, (await Send("POST", $"/api/Opportunities/{f.Opportunity}/reopen", body, await Csrf())).Status);
        Assert.Equal(404, (await Part04Reopen(int.MaxValue)).Status);
        Assert.Equal(200, (await Part04Reopen(f.Opportunity, " " + new string('x', 2000) + " ")).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        var history = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Activities.SingleAsync(a => a.Subject == "Opportunity reopened");
        Assert.Equal(adminId, history.CreatedById); Assert.Equal(f.Agent, history.UserId);
        Assert.Equal(2000, JsonDocument.Parse(history.Details!).RootElement.GetProperty("reason").GetString()!.Length);
        Assert.Equal(403, (await Send("DELETE", $"/api/Activities/{history.Id}", csrf: await Csrf())).Status);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Reserved")]
    [InlineData("Withdrawn")]
    [InlineData("Sold")]
    public async Task Part04_lost_reopen_rejects_non_available_listing_without_history(string status)
    {
        var f = await Part02Fixture(); Assert.Equal(200, (await Part03Lost(f.Opportunity, "Other")).Status);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Properties.Where(p => p.Id == f.Property).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, status));
        await SignInWorkflowManager(); Assert.Equal(409, (await Part04Reopen(f.Opportunity)).Status);
        Assert.Equal("Lost", (await Send("GET", $"/api/Opportunities/{f.Opportunity}")).Json.GetProperty("status").GetString());
        await using var verify = _app.Services.CreateAsyncScope();
        Assert.Empty(await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().Activities.Where(a => a.Subject == "Opportunity reopened").ToListAsync());
    }

    [Theory]
    [InlineData("Available")]
    [InlineData("Reserved")]
    [InlineData("Draft")]
    [InlineData("Withdrawn")]
    public async Task Part04_won_reopen_rejects_non_sold_listing(string status)
    {
        var f = await Part02Fixture(); Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        Assert.Equal(200, (await Part03Won(f.Opportunity, 100)).Status);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Properties.Where(p => p.Id == f.Property).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, status));
        await SignInWorkflowManager(); Assert.Equal(409, (await Part04Reopen(f.Opportunity)).Status);
        Assert.Equal("Won", (await Send("GET", $"/api/Opportunities/{f.Opportunity}")).Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Part04_reopen_rejects_duplicate_open_pair_and_ambiguous_winning_identity()
    {
        var f = await Part02Fixture(); Assert.Equal(200, (await Part03Lost(f.Opportunity, "Other")).Status);
        Assert.Equal(201, (await Send("POST", "/api/Opportunities", new { buyerContactId = f.Buyer, propertyId = f.Property }, await Csrf())).Status);
        await SignInWorkflowManager(); Assert.Equal(409, (await Part04Reopen(f.Opportunity)).Status);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var o in await db.Opportunities.ToListAsync()) { o.Status = "Won"; o.StageId = 6; o.FinalPrice = 100; o.LostReason = null; o.ClosedAt = DateTime.UtcNow; }
            (await db.Properties.SingleAsync()).Status = "Sold"; await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Part04Reopen(f.Opportunity)).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var persisted = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, await persisted.Opportunities.CountAsync(o => o.Status == "Won"));
        Assert.Empty(await persisted.Activities.Where(a => a.Subject == "Opportunity reopened").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Part04_reopen_preserves_other_client_links_and_manual_inactive_priority(bool inactive)
    {
        var f = await Part02Fixture(); Assert.Equal(200, (await Part03Lost(f.Opportunity, "Other")).Status);
        var secondProperty = await WorkflowDraft(f.Seller); Assert.Equal(200, (await WorkflowPublish(secondProperty, f.Seller, f.Agent)).Status);
        var second = await Send("POST", "/api/Opportunities", new { buyerContactId = f.Buyer, propertyId = secondProperty }, await Csrf());
        var secondId = second.Json.GetProperty("id").GetInt32();
        Assert.Equal(200, (await Part02Move(secondId, f.Buyer, secondProperty, f.Agent, 6)).Status);
        Assert.Equal(200, (await Part03Won(secondId, 100)).Status);
        if (inactive) Assert.Equal(200, (await Send("PUT", $"/api/Contacts/{f.Buyer}", new { firstName = "Workflow", lastName = "Buyer", email = "part02-buyer@example.com", type = "Buyer", source = "Website", ownerId = f.Agent, isManuallyInactive = true }, await Csrf())).Status);
        await SignInWorkflowManager(); Assert.Equal(200, (await Part04Reopen(f.Opportunity)).Status);
        Assert.Equal(inactive ? "Inactive" : "Client", (await Send("GET", $"/api/Contacts/{f.Buyer}")).Json.GetProperty("status").GetString());
        Assert.Equal("Client", (await Send("GET", $"/api/Contacts/{f.Seller}")).Json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Part04_reopen_history_or_contact_failure_rolls_back_sale_reset(bool failContact)
    {
        var f = await Part02Fixture(); Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        Assert.Equal(200, (await Part03Won(f.Opportunity, 100)).Status); await SignInWorkflowManager();
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync(failContact ? """
                CREATE TRIGGER fail_reopen_contact BEFORE UPDATE OF "Status" ON "Contacts"
                WHEN NEW."Status" = 'Active' BEGIN SELECT RAISE(ABORT, 'fixture reopen contact failure'); END;
                """ : """
                CREATE TRIGGER fail_reopen_history BEFORE INSERT ON "Activities"
                WHEN NEW."Subject" = 'Opportunity reopened' BEGIN SELECT RAISE(ABORT, 'fixture reopen history failure'); END;
                """);
        Assert.Equal(500, (await Part04Reopen(f.Opportunity)).Status);
        await using var verify = _app.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var target = await db.Opportunities.SingleAsync(); Assert.Equal("Won", target.Status); Assert.Equal(6, target.StageId); Assert.Equal(100, target.FinalPrice); Assert.NotNull(target.ClosedAt);
        Assert.Equal("Sold", (await db.Properties.SingleAsync()).Status); Assert.All(await db.Contacts.ToListAsync(), c => Assert.Equal("Client", c.Status));
        Assert.Empty(await db.Activities.Where(a => a.Subject == "Opportunity reopened").ToListAsync());
    }

    [Fact]
    public async Task Part04_reopened_assigned_private_buyer_remains_private_and_renamed_inquiry_uses_code()
    {
        var f = await Part02Fixture();
        var second = await Send("POST", "/api/auth/register", Account(email: "part04-assigned@example.com"), await Csrf());
        var secondId = second.Json.GetProperty("id").GetInt32();
        await SignInWorkflowManager();
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, secondId, 2)).Status);
        Assert.Equal(200, (await Part03Lost(f.Opportunity, "Other")).Status);
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PipelineStages.Where(s => s.Code == "INQUIRY").ExecuteUpdateAsync(set => set.SetProperty(s => s.Name, "Initial discussion"));
        var reopened = await Part04Reopen(f.Opportunity); Assert.Equal(200, reopened.Status);
        Assert.Equal("Initial discussion", reopened.Json.GetProperty("stage").GetProperty("name").GetString());
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "part04-assigned@example.com", password = Password }, await Csrf())).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Contacts/{f.Buyer}")).Status);
        var visible = await Send("GET", $"/api/Opportunities/{f.Opportunity}");
        Assert.Equal(JsonValueKind.Null, visible.Json.GetProperty("buyerContact").ValueKind);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, secondId, 2)).Status);
        var history = Assert.Single((await Send("GET", "/api/Activities")).Json.EnumerateArray().Where(a => a.GetProperty("subject").GetString() == "Opportunity reopened"));
        Assert.Equal(secondId, history.GetProperty("userId").GetInt32());
        Assert.Equal(403, (await Send("DELETE", $"/api/Activities/{history.GetProperty("id").GetInt32()}", csrf: await Csrf())).Status);
    }

    [Fact]
    public async Task Part04_release_then_withdraw_and_republish_uses_existing_lifecycle_gates()
    {
        var f = await Part02Fixture();
        async Task<TestResponse> Withdraw() => await Send("PUT", $"/api/Properties/{f.Property}", new { address = "Part 04 listing", city = "Makati", propertyType = "Condominium", sellerContactId = f.Seller, listingAgentId = f.Agent, price = 2500000, status = "Withdrawn" }, await Csrf());
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 6)).Status);
        Assert.Equal(400, (await Withdraw()).Status);
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 5)).Status);
        Assert.Equal("Reserved", (await Send("GET", $"/api/Properties/{f.Property}")).Json.GetProperty("status").GetString());
        Assert.Equal(200, (await Part02Move(f.Opportunity, f.Buyer, f.Property, f.Agent, 4)).Status);
        Assert.Equal(200, (await Part03Lost(f.Opportunity, "Other")).Status);
        Assert.Equal(200, (await Withdraw()).Status);
        Assert.Equal(400, (await WorkflowPublish(f.Property, f.Seller, f.Agent, null)).Status);
        await SignInWorkflowManager(); Assert.Equal(409, (await Part04Reopen(f.Opportunity)).Status);
        Assert.Equal(200, (await WorkflowPublish(f.Property, f.Seller, f.Agent)).Status);
        Assert.Equal(200, (await Part04Reopen(f.Opportunity)).Status);
    }
}
