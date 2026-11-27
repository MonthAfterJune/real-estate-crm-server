using Microsoft.AspNetCore.Authentication;
using System.Threading.RateLimiting;
using CRM.Data;
using CRM.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CRM.Authentication;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddCrmAuthentication(this IServiceCollection services, bool development)
    {
        services.AddIdentityCore<User>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Password.RequiredUniqueChars = 3;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        services.AddScoped<IUserClaimsPrincipalFactory<User>, CrmClaimsPrincipalFactory>();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
            {
                options.Cookie.Name = development ? "Meridian.Session" : "__Host-Meridian.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = development ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = async context =>
                    {
                        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<User>>();
                        var user = context.Principal is null ? null : await users.GetUserAsync(context.Principal);
                        if (user is null || !user.IsActive)
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                            return;
                        }
                        await SecurityStampValidator.ValidatePrincipalAsync(context);
                    },
                    OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; }
                };
            });
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy("ManageUsers", policy => policy.RequireRole("admin"));
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = development ? "Meridian.Csrf" : "__Host-Meridian.Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = development ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
            options.Cookie.Path = "/";
        });
        services.AddScoped<ApiAntiforgeryFilter>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        return services;
    }
}
