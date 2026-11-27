using System.Security.Claims;
using CRM.Data;
using CRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CRM.Authentication;

// The CRM already has one role per user. Keep that relationship instead of introducing a second role system.
public sealed class CrmClaimsPrincipalFactory : UserClaimsPrincipalFactory<User>
{
    private readonly ApplicationDbContext _db;
    public CrmClaimsPrincipalFactory(UserManager<User> users, IOptions<IdentityOptions> options, ApplicationDbContext db)
        : base(users, options) => _db = db;

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var role = await _db.Roles.Where(r => r.Id == user.RoleId).Select(r => r.Name).SingleAsync();
        identity.AddClaim(new Claim(ClaimTypes.Role, role.ToLowerInvariant()));
        identity.AddClaim(new Claim("full_name", user.FullName));
        return identity;
    }
}
