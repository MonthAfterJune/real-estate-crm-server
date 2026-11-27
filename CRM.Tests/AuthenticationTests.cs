using System.Net;
using System.Text;
using System.Text.Json;
using CRM.Authentication;
using CRM.Controllers;
using CRM.Data;
using CRM.ErrorHandling;
using CRM.Mapping;
using CRM.Models;
using CRM.Services;
using CRM.Services.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CRM.Tests;

// Run real Identity, cookie/CSRF middleware, controllers and EF against an isolated
// relational database. No production data, network listener or plaintext passwords.
public sealed partial class AuthenticationTests : IAsyncLifetime
{
    private const string Password = "a long meridian passphrase";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly WebApplication _app;
    private readonly RequestDelegate _pipeline;
    private readonly Dictionary<string, string> _cookies = new();

    public AuthenticationTests()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AuthController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.Configuration["DefaultCommissionRate"] = "0.03";
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddScoped<ApplicationDbContext>(sp => new AuthenticationSqliteContext(
            sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
        builder.Services.AddCrmAuthentication(development: true);
        builder.Services.AddControllers(o => o.Filters.AddService<ApiAntiforgeryFilter>())
            .AddApplicationPart(typeof(AuthController).Assembly);
        builder.Services.AddAutoMapper(typeof(MappingProfile));
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<IRoleService, RoleService>();
        builder.Services.AddScoped<IPropertyService, PropertyService>();
        builder.Services.AddScoped<IContactService, ContactService>();
        builder.Services.AddScoped<IOpportunityService, OpportunityService>();
        builder.Services.AddScoped<IPipelineStageService, PipelineStageService>();
        builder.Services.AddScoped<IActivityService, ActivityService>();
        builder.Services.AddScoped<INoteService, NoteService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<StaffAccess>();
        builder.Services.AddScoped<Part01Workflow>();
        builder.Services.AddApiErrorHandling();
        _app = builder.Build();
        _app.UseApiErrorHandling();
        // Exceptions propagate to the test runner with their original stack trace.
        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRateLimiter();
        ((IApplicationBuilder)_app).UseEndpoints(e => e.MapControllers());
        _pipeline = ((IApplicationBuilder)_app).Build();
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Roles.AddRange(new Role { Name = "agent" }, new Role { Name = "buyer" }, new Role { Name = "seller" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_requests_cannot_access_sessions_or_CRM_data()
    {
        Assert.Equal(401, (await Send("GET", "/api/auth/me")).Status);
        Assert.Equal(401, (await Send("GET", "/api/Users")).Status);
        Assert.Equal(401, (await Send("GET", "/api/Properties")).Status);
    }

    [Fact]
    public async Task Registration_hashes_password_and_sets_HttpOnly_cookie_and_session()
    {
        var response = await Register();
        Assert.Equal(201, response.Status);
        Assert.Equal("agent", response.Json.GetProperty("role").GetString());
        Assert.False(response.Json.TryGetProperty("passwordHash", out _));
        var cookie = Assert.Single(response.SetCookies, c => c.StartsWith("Meridian.Session="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.DoesNotContain("expires=", cookie.ToLowerInvariant());
        var me = await Send("GET", "/api/auth/me");
        Assert.Equal(200, me.Status);
        Assert.Equal("Taylor Agent", me.Json.GetProperty("fullName").GetString());
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByEmailAsync("TAYLOR@example.com"))!;
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.True(await users.CheckPasswordAsync(user, Password));
    }

    [Fact]
    public async Task Missing_or_tampered_CSRF_tokens_do_not_create_accounts()
    {
        var noToken = await Send("POST", "/api/auth/register", Account());
        Assert.Equal(400, noToken.Status);
        Assert.Equal("csrf_token_invalid", noToken.Json.GetProperty("code").GetString());
        await Csrf();
        Assert.Equal(400, (await Send("POST", "/api/auth/register", Account(), "tampered")).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task Logout_clears_session_and_login_restores_it_case_insensitively()
    {
        Assert.Equal(201, (await Register()).Status);
        var logout = await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(204, logout.Status);
        Assert.Equal(401, (await Send("GET", "/api/auth/me")).Status);
        var wrong = await Send("POST", "/api/auth/login", new { email = "taylor@example.com", password = "wrong password" }, await Csrf());
        Assert.Equal(401, wrong.Status);
        var login = await Send("POST", "/api/auth/login", new { email = "TAYLOR@example.com", password = Password, rememberMe = true }, await Csrf());
        Assert.Equal(200, login.Status);
        Assert.Contains("expires=", Assert.Single(login.SetCookies, c => c.StartsWith("Meridian.Session=")).ToLowerInvariant());
        Assert.Equal(200, (await Send("GET", "/api/auth/me")).Status);
    }

    [Fact]
    public async Task Duplicate_email_short_password_and_admin_signup_are_rejected()
    {
        Assert.Equal(201, (await Register()).Status);
        var token = await Csrf();
        Assert.Equal(400, (await Send("POST", "/api/auth/register", Account(email: "TAYLOR@example.com"), token)).Status);
        Assert.Equal(400, (await Send("POST", "/api/auth/register", Account(email: "other@example.com", password: "short"), token)).Status);
        Assert.Equal(400, (await Send("POST", "/api/auth/register", Account(email: "admin@example.com", role: "admin"), token)).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task Five_failed_passwords_lock_account_and_unknown_email_has_same_error()
    {
        await Register();
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        var token = await Csrf();
        var unknown = await Send("POST", "/api/auth/login", new { email = "unknown@example.com", password = Password }, token);
        for (var i = 0; i < 5; i++)
        {
            var wrong = await Send("POST", "/api/auth/login", new { email = "taylor@example.com", password = "incorrect passphrase" }, token);
            Assert.Equal(401, wrong.Status);
            Assert.Equal(unknown.Json.GetProperty("detail").GetString(), wrong.Json.GetProperty("detail").GetString());
        }
        Assert.Equal(401, (await Send("POST", "/api/auth/login", new { email = "taylor@example.com", password = Password }, token)).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByEmailAsync("taylor@example.com"))!));
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("seller")]
    public async Task Role_permissions_are_enforced_by_API(string role)
    {
        Assert.Equal(201, (await Register(role)).Status);
        Assert.Equal(200, (await Send("GET", "/api/auth/me")).Status);
        Assert.Equal(403, (await Send("GET", "/api/Users")).Status);
        Assert.Equal(403, (await Send("GET", "/api/Properties")).Status);
    }

    [Fact]
    public async Task Agent_can_read_directory_but_cannot_change_roles_or_users()
    {
        await Register();
        Assert.Equal(200, (await Send("GET", "/api/Users")).Status);
        Assert.Equal(403, (await Send("POST", "/api/Users", new { fullName = "Injected", email = "injected@example.com", roleId = 1 }, await Csrf())).Status);
        Assert.Equal(403, (await Send("POST", "/api/Roles", new { name = "admin" }, await Csrf())).Status);
    }

    [Fact]
    public async Task Directory_update_preserves_password_and_rotates_stamp_on_role_change()
    {
        await Register();
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByEmailAsync("taylor@example.com"))!;
        var passwordHash = user.PasswordHash;
        var stamp = user.SecurityStamp;
        var service = scope.ServiceProvider.GetRequiredService<IUserService>();
        await service.UpdateAsync(user.Id, new User { FullName = "Taylor Buyer", Email = "taylor@example.com", RoleId = 2 });
        Assert.Equal(passwordHash, user.PasswordHash);
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.True(await users.CheckPasswordAsync(user, Password));
    }

    [Fact]
    public async Task Authentication_requests_are_rate_limited()
    {
        var token = await Csrf();
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, (await Send("POST", "/api/auth/login", new { email = "none@example.com", password = Password }, token)).Status);
        Assert.Equal(429, (await Send("POST", "/api/auth/login", new { email = "none@example.com", password = Password }, token)).Status);
    }

    [Fact]
    public async Task Agent_can_create_an_owned_seller_contact_and_persist_a_property()
    {
        var agent = await Register();
        Assert.Equal(201, agent.Status);
        var agentId = agent.Json.GetProperty("id").GetInt32();
        var seller = await Send("POST", "/api/Contacts", new {
            firstName = "Sam", lastName = "Owner", email = "seller@example.com", type = "Seller",
            source = "Referral", ownerId = agentId
        }, await Csrf());
        Assert.Equal(201, seller.Status);
        var sellerId = seller.Json.GetProperty("id").GetInt32();
        Assert.Equal("Taylor Agent", seller.Json.GetProperty("owner").GetProperty("fullName").GetString());
        var created = await Send("POST", "/api/Properties", new {
            address = "10 Seller Contact Street", city = "Manila", propertyType = "House", price = 2500000, status = "Draft", bedrooms = 2,
            sellerContactId = sellerId, listingAgentId = agentId
        }, await Csrf());
        Assert.Equal(201, created.Status);
        var id = created.Json.GetProperty("id").GetInt32();
        var published = await Send("PUT", $"/api/Properties/{id}", new {
            address = "10 Seller Contact Street", city = "Manila", propertyType = "House", price = 2500000, status = "Available", bedrooms = 2,
            sellerContactId = sellerId, listingAgentId = agentId
        }, await Csrf());
        Assert.Equal(200, published.Status);
        Assert.Equal(sellerId, created.Json.GetProperty("sellerContactId").GetInt32());
        Assert.Equal("Sam", created.Json.GetProperty("sellerContact").GetProperty("firstName").GetString());
        Assert.False(created.Json.TryGetProperty("sellerUserId", out _));
        var fetched = await Send("GET", $"/api/Properties/{id}");
        Assert.Equal(200, fetched.Status);
        Assert.Equal(sellerId, fetched.Json.GetProperty("sellerContactId").GetInt32());
        var buyer = await Send("POST", "/api/Contacts", new {
            firstName = "Pat", lastName = "Buyer", email = "pat@example.com", type = "Buyer", source = "Website", ownerId = agentId
        }, await Csrf());
        Assert.Equal(201, buyer.Status);
        var buyerId = buyer.Json.GetProperty("id").GetInt32();
        var stage = await Send("GET", "/api/PipelineStages/1");
        Assert.Equal(200, stage.Status);
        var deal = await Send("POST", "/api/Opportunities", new {
            buyerContactId = buyerId, propertyId = id, agentId, stageId = stage.Json.GetProperty("id").GetInt32(), offerAmount = 2400000
        }, await Csrf());
        Assert.Equal(201, deal.Status);
        var dealId = deal.Json.GetProperty("id").GetInt32();
        var activity = await Send("POST", "/api/Activities", new {
            type = "Call", subject = "Discuss offer", contactId = buyerId, opportunityId = dealId, userId = agentId, completedAt = DateTime.UtcNow
        }, await Csrf());
        Assert.Equal(201, activity.Status);
        Assert.False(activity.Json.TryGetProperty("leadId", out _));
        var note = await Send("POST", "/api/Notes", new { authorId = agentId, entityType = "Opportunity", entityId = dealId, body = "Buyer requested a viewing." }, await Csrf());
        Assert.Equal(201, note.Status);
        var rejectedDeal = await Send("POST", "/api/Opportunities", new {
            buyerContactId = sellerId, propertyId = id, agentId, stageId = stage.Json.GetProperty("id").GetInt32(), offerAmount = 2400000
        }, await Csrf());
        Assert.Equal(400, rejectedDeal.Status);
        Assert.True(rejectedDeal.Json.GetProperty("errors").TryGetProperty("BuyerContactId", out _));
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(sellerId, (await db.Properties.FindAsync(id))!.SellerContactId);
        Assert.Equal((agentId, "Lead", "Referral"), ((await db.Contacts.FindAsync(sellerId))!.OwnerId,
            seller.Json.GetProperty("status").GetString(), seller.Json.GetProperty("source").GetString()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task Invalid_contact_owner_returns_field_errors(int ownerId)
    {
        await Register();
        var response = await Send("POST", "/api/Contacts", new {
            firstName = "Alex", lastName = "Lee", email = "alex@example.com", type = "Buyer", source = "Website", ownerId
        }, await Csrf());
        Assert.Equal(400, response.Status);
        Assert.True(response.Json.GetProperty("errors").TryGetProperty("OwnerId", out _));
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Contacts.ToListAsync());
    }
    [Theory]
    [InlineData("source", "Source")]
    public async Task Contact_requires_lifecycle_fields_and_owner(string omitted, string errorKey)
    {
        var account = await Register();
        var request = new Dictionary<string, object> {
            ["firstName"] = "Alex", ["lastName"] = "Lee", ["type"] = "Buyer", ["email"] = "alex@example.com",
            ["source"] = "Website", ["ownerId"] = account.Json.GetProperty("id").GetInt32()
        };
        request.Remove(omitted);
        var response = await Send("POST", "/api/Contacts", request, await Csrf());
        Assert.Equal(400, response.Status);
        Assert.True(response.Json.GetProperty("errors").TryGetProperty(errorKey, out _));
    }

    [Fact]
    public async Task Contact_update_preserves_id_and_changes_owner_status_and_source()
    {
        var account = await Register();
        var firstOwnerId = account.Json.GetProperty("id").GetInt32();
        await using var scope = _app.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var secondOwner = new User { FullName = "Second Agent", Email = "second@example.com", UserName = "second@example.com", RoleId = 1 };
        Assert.True((await manager.CreateAsync(secondOwner)).Succeeded);
        var contact = await Send("POST", "/api/Contacts", new {
            firstName = "Alex", lastName = "Lee", email = "alex@example.com", type = "Buyer", source = "Website", ownerId = firstOwnerId
        }, await Csrf());
        Assert.Equal(201, contact.Status);
        var id = contact.Json.GetProperty("id").GetInt32();
        await SignInWorkflowManager();
        var response = await Send("PUT", $"/api/Contacts/{id}", new {
            firstName = "Alex", lastName = "Lee", email = "alex@example.com", type = "Buyer", isManuallyInactive = true, source = "Other", ownerId = secondOwner.Id
        }, await Csrf());
        Assert.Equal(200, response.Status);
        Assert.Equal(id, response.Json.GetProperty("id").GetInt32());
        Assert.Equal(secondOwner.Id, response.Json.GetProperty("ownerId").GetInt32());
        Assert.Equal("Second Agent", response.Json.GetProperty("owner").GetProperty("fullName").GetString());
        Assert.Equal("Inactive", response.Json.GetProperty("status").GetString());
        Assert.Equal("Other", response.Json.GetProperty("source").GetString());
        Assert.Equal("Buyer", response.Json.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Obsolete_Leads_route_is_removed_and_seller_signup_does_not_create_a_contact()
    {
        await Register("seller");
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Contacts.ToListAsync());
        Assert.Equal(404, (await Send("GET", "/api/Leads")).Status);
    }

    [Fact]
    public async Task V2_contact_only_activity_retains_author_and_audits_and_soft_deletes()
    {
        var account = await Register();
        var agentId = account.Json.GetProperty("id").GetInt32();
        var contact = await Send("POST", "/api/Contacts", new {
            firstName = "Alex", lastName = "Lee", type = "Both", email = "Alex@Example.com", source = "Website", ownerId = agentId
        }, await Csrf());
        Assert.Equal(201, contact.Status);
        Assert.Equal("alex@example.com", contact.Json.GetProperty("email").GetString());
        var contactId = contact.Json.GetProperty("id").GetInt32();
        var scheduled = DateTime.UtcNow.AddDays(1);
        var created = await Send("POST", "/api/Activities", new {
            type = "Task", subject = "Call Alex", contactId, userId = agentId, scheduledAt = scheduled
        }, await Csrf());
        Assert.Equal(201, created.Status);
        Assert.Equal(agentId, created.Json.GetProperty("createdById").GetInt32());
        Assert.Equal(JsonValueKind.Null, created.Json.GetProperty("opportunityId").ValueKind);
        var id = created.Json.GetProperty("id").GetInt32();
        var updated = await Send("PUT", $"/api/Activities/{id}", new {
            type = "Task", subject = "Called Alex", contactId, userId = agentId, scheduledAt = scheduled, completedAt = DateTime.UtcNow
        }, await Csrf());
        Assert.Equal(200, updated.Status);
        Assert.Equal(created.Json.GetProperty("createdAt").GetString(), updated.Json.GetProperty("createdAt").GetString());
        Assert.Equal(agentId, updated.Json.GetProperty("createdById").GetInt32());
        Assert.Equal(204, (await Send("DELETE", $"/api/Activities/{id}", csrf: await Csrf())).Status);
        Assert.Equal(404, (await Send("GET", $"/api/Activities/{id}")).Status);
        Assert.Equal(400, (await Send("DELETE", $"/api/Contacts/{contactId}", csrf: await Csrf())).Status);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True((await db.Activities.IgnoreQueryFilters().SingleAsync(a => a.Id == id)).IsDeleted);
        Assert.NotNull(await db.Contacts.SingleOrDefaultAsync(c => c.Id == contactId));
    }

    [Fact]
    public async Task External_clients_cannot_set_contact_status_or_forge_generated_activity_history()
    {
        var account = await Register();
        var agentId = account.Json.GetProperty("id").GetInt32();
        var forged = await Send("POST", "/api/Contacts", new {
            firstName = "Alex", lastName = "Lee", type = "Buyer", email = "alex@example.com", source = "Website", ownerId = agentId, status = "Client"
        }, await Csrf());
        Assert.Equal(400, forged.Status);
        var history = await Send("POST", "/api/Activities", new {
            type = "Stage Change", subject = "Forged", contactId = 1, userId = agentId, createdById = agentId,
            completedAt = DateTime.UtcNow, isSystemGenerated = true, fromStageId = 1, toStageId = 6
        }, await Csrf());
        Assert.Equal(400, history.Status);
    }

    [Fact]
    public async Task Assigned_agent_can_complete_another_authors_task_without_editing_its_metadata()
    {
        var account = await Register();
        var authorId = account.Json.GetProperty("id").GetInt32();
        int assigneeId;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var assignee = new User { FullName = "Assigned Agent", Email = "assigned@example.com", UserName = "assigned@example.com", RoleId = 1 };
            Assert.True((await users.CreateAsync(assignee, Password)).Succeeded);
            assigneeId = assignee.Id;
        }
        authorId = await SignInWorkflowManager();
        var contact = await Send("POST", "/api/Contacts", new {
            firstName = "Alex", lastName = "Lee", type = "Buyer", email = "alex@example.com", source = "Website", ownerId = assigneeId
        }, await Csrf());
        Assert.Equal(201, contact.Status);
        var contactId = contact.Json.GetProperty("id").GetInt32();
        var scheduled = DateTime.UtcNow.AddDays(1);
        var task = await Send("POST", "/api/Activities", new {
            type = "Task", subject = "Call Alex", contactId, userId = assigneeId, scheduledAt = scheduled
        }, await Csrf());
        Assert.Equal(201, task.Status);
        var id = task.Json.GetProperty("id").GetInt32();
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "assigned@example.com", password = Password }, await Csrf())).Status);
        var completed = DateTime.UtcNow;
        Assert.Equal(403, (await Send("PUT", $"/api/Activities/{id}", new {
            type = "Task", subject = "Unauthorized subject", contactId, userId = assigneeId, scheduledAt = scheduled, completedAt = completed
        }, await Csrf())).Status);
        var response = await Send("PUT", $"/api/Activities/{id}", new {
            type = "Task", subject = "Call Alex", contactId, userId = assigneeId, scheduledAt = scheduled, completedAt = completed
        }, await Csrf());
        Assert.Equal(200, response.Status);
        Assert.Equal(authorId, response.Json.GetProperty("createdById").GetInt32());
        Assert.Equal(assigneeId, response.Json.GetProperty("userId").GetInt32());
        Assert.Equal(403, (await Send("PUT", $"/api/Activities/{id}", new {
            type = "Task", subject = "Changed after completion", contactId, userId = assigneeId, scheduledAt = scheduled, completedAt = completed
        }, await Csrf())).Status);
    }

    [Fact]
    public async Task Manager_can_edit_completed_metadata_without_completing_for_another_performer()
    {
        var account = await Register();
        var performerId = account.Json.GetProperty("id").GetInt32();
        var contact = await Send("POST", "/api/Contacts", new {
            firstName = "Alex", lastName = "Lee", type = "Buyer", email = "alex@example.com", source = "Website", ownerId = performerId
        }, await Csrf());
        Assert.Equal(201, contact.Status);
        var contactId = contact.Json.GetProperty("id").GetInt32();
        var completed = DateTime.UtcNow;
        var activity = await Send("POST", "/api/Activities", new {
            type = "Call", subject = "Called Alex", contactId, userId = performerId, completedAt = completed
        }, await Csrf());
        Assert.Equal(201, activity.Status);
        var id = activity.Json.GetProperty("id").GetInt32();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var role = new Role { Name = "manager" }; db.Roles.Add(role); await db.SaveChangesAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var manager = new User { FullName = "Manager", Email = "manager@example.com", UserName = "manager@example.com", RoleId = role.Id };
            Assert.True((await users.CreateAsync(manager, Password)).Succeeded);
        }
        await Send("POST", "/api/auth/logout", csrf: await Csrf());
        Assert.Equal(200, (await Send("POST", "/api/auth/login", new { email = "manager@example.com", password = Password }, await Csrf())).Status);
        var edited = await Send("PUT", $"/api/Activities/{id}", new {
            type = "Call", subject = "Corrected call summary", contactId, userId = performerId, completedAt = completed
        }, await Csrf());
        Assert.Equal(200, edited.Status);
        Assert.Equal("Corrected call summary", edited.Json.GetProperty("subject").GetString());
        Assert.Equal(performerId, edited.Json.GetProperty("userId").GetInt32());
        Assert.Equal(400, (await Send("PUT", $"/api/Activities/{id}", new {
            type = "Call", subject = "Corrected call summary", contactId, userId = performerId, scheduledAt = DateTime.UtcNow.AddDays(1)
        }, await Csrf())).Status);
        Assert.Equal(400, (await Send("PUT", $"/api/Activities/{id}", new {
            type = "Call", subject = "Corrected call summary", contactId, userId = performerId + 999, completedAt = completed
        }, await Csrf())).Status);
    }

    [Fact]
    public async Task Invalid_note_target_type_and_nonpositive_id_return_field_errors()
    {
        var account = await Register();
        var authorId = account.Json.GetProperty("id").GetInt32();
        foreach (var method in new[] { "POST", "PUT" })
        {
            var path = method == "POST" ? "/api/Notes" : "/api/Notes/1";
            var response = await Send(method, path, new { authorId, entityType = "User", entityId = 0, body = "Invalid target" }, await Csrf());
            Assert.Equal(400, response.Status);
            Assert.True(response.Json.GetProperty("errors").TryGetProperty("EntityType", out _));
            Assert.True(response.Json.GetProperty("errors").TryGetProperty("EntityId", out _));
        }
    }

    [Fact]
    public async Task Deleting_user_deactivates_retains_identity_and_rejects_cookie_and_login()
    {
        var account = await Register();
        var agentId = account.Json.GetProperty("id").GetInt32();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IUserService>().DeleteAsync(agentId));
            var user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(u => u.Id == agentId);
            Assert.False(user.IsActive);
            Assert.False(user.IsDeleted);
            Assert.NotNull(user.PasswordHash);
        }
        Assert.Equal(401, (await Send("GET", "/api/auth/me")).Status);
        Assert.Equal(401, (await Send("POST", "/api/auth/login", new { email = "taylor@example.com", password = Password }, await Csrf())).Status);
    }

    private object Account(string email = "taylor@example.com", string password = Password, string role = "agent") =>
        new { fullName = "Taylor Agent", email, password, role, acceptTerms = true };
    private async Task<TestResponse> Register(string role = "agent") =>
        await Send("POST", "/api/auth/register", Account(role: role), await Csrf());
    private async Task<string> Csrf()
    {
        var response = await Send("GET", "/api/auth/csrf");
        Assert.Equal(200, response.Status);
        return response.Json.GetProperty("token").GetString()!;
    }

    private async Task<TestResponse> Send(string method, string path, object? body = null, string? csrf = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers.Accept = "application/json";
        context.Request.Headers.Cookie = string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}"));
        if (csrf is not null) context.Request.Headers["X-CSRF-TOKEN"] = csrf;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(body is null ? "" : JsonSerializer.Serialize(body)));
        await using var output = new MemoryStream();
        context.Response.Body = output;
        if (body is not null)
        {
            context.Request.Body = input;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = input.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
        }
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = context;
        try { await _pipeline(context); }
        finally { accessor.HttpContext = null; }
        var setCookies = context.Response.Headers.SetCookie.Select(c => c!).ToArray();
        foreach (var cookie in setCookies)
        {
            var pair = cookie.Split(';', 2)[0].Split('=', 2);
            if (pair[1].Length == 0) _cookies.Remove(pair[0]);
            else _cookies[pair[0]] = pair[1];
        }
        return new(context.Response.StatusCode, Encoding.UTF8.GetString(output.ToArray()), setCookies);
    }
    private sealed record TestResponse(int Status, string Body, string[] SetCookies)
    {
        public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Body);
    }
    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }

    // SQLite defaults decimal to TEXT. Numeric affinity keeps the real rate CHECK
    // meaningful at 1 ("1.0" otherwise compares above "1"). PostgreSQL provider
    // precision/concurrency still require isolated real-provider checks.
    private sealed class AuthenticationSqliteContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Opportunity>().Property(o => o.CommissionRate).HasColumnType("numeric");
        }
    }
}






