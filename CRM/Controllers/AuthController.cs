using CRM.Data;
using CRM.DTOs.Requests;
using CRM.DTOs.Responses;
using CRM.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CRM.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(UserManager<User> users, SignInManager<User> signIn, ApplicationDbContext db, IAntiforgery antiforgery) : ControllerBase
{
    [AllowAnonymous, HttpGet("csrf")]
    public IActionResult Csrf() => Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [AllowAnonymous, EnableRateLimiting("auth"), HttpPost("register")]
    public async Task<ActionResult<AuthUserResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name.ToLower() == request.Role, cancellationToken);
        if (role is null) return Problem(statusCode: 503, detail: "Account setup is unavailable. Please try again later.");
        var user = new User { FullName = request.FullName.Trim(), Email = email, UserName = email, RoleId = role.Id };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                var field = error.Code.StartsWith("Password") ? nameof(request.Password) : nameof(request.Email);
                var message = error.Code is "DuplicateEmail" or "DuplicateUserName"
                    ? "An account with this email already exists. Try logging in." : error.Description;
                ModelState.AddModelError(field, message);
            }
            return ValidationProblem(ModelState);
        }
        await signIn.SignInAsync(user, isPersistent: false);
        return StatusCode(StatusCodes.Status201Created, ToResponse(user, role.Name));
    }

    [AllowAnonymous, EnableRateLimiting("auth"), HttpPost("login")]
    public async Task<ActionResult<AuthUserResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive) return InvalidCredentials();
        var role = await db.Roles.Where(r => r.Id == user.RoleId).Select(r => r.Name).SingleOrDefaultAsync(cancellationToken);
        if (role is null || !new[] { "agent", "manager", "buyer", "seller", "admin" }.Contains(role.ToLowerInvariant())) return InvalidCredentials();
        var result = await signIn.PasswordSignInAsync(user, request.Password, request.RememberMe, lockoutOnFailure: true);
        // Keep unknown email, wrong password and locked-out responses identical.
        if (!result.Succeeded) return InvalidCredentials();
        return Ok(ToResponse(user, role));
    }

    [HttpGet("me")]
    public async Task<ActionResult<AuthUserResponse>> Me(CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        var role = await db.Roles.Where(r => r.Id == user.RoleId).Select(r => r.Name).SingleAsync(cancellationToken);
        return Ok(ToResponse(user, role));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    private ObjectResult InvalidCredentials() => Problem(statusCode: 401,
        title: "Unable to log in.", detail: "The email or password is incorrect, or this account is temporarily locked.");
    private static AuthUserResponse ToResponse(User user, string role) =>
        new(user.Id, user.FullName, user.Email!, role.ToLowerInvariant());
}
