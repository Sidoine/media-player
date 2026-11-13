using System.Security.Claims;
using media_player.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace media_player.Controllers;

[ApiController]
[Route("api/users")]
public sealed class AccountsController(UserAccountStore users) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(new
    {
        enabled = (await users.GetAllAsync(ct)).Count > 0,
        authenticated = User.Identity?.IsAuthenticated == true,
        administrator = User.IsInRole("Administrator"),
        userName = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null
    });

    [HttpGet]
    [Authorize(Policy = "AdminWhenMultiUserEnabled")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await users.GetAllAsync(ct)).Select(user => new { user.UserName, user.Role }));

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.UserName) || input.UserName.Trim().Length > 64
            || input.UserName.Any(char.IsControl) || input.Password is null || input.Password.Length is < 10 or > 256)
            return BadRequest(new { error = "Indiquez un nom et un mot de passe d’au moins 10 caractères." });
        var existing = await users.GetAllAsync(ct);
        if (existing.Count > 0 && User.Identity?.IsAuthenticated != true) return Unauthorized();
        if (existing.Count > 0 && !User.IsInRole("Administrator")) return Forbid();
        try
        {
            var account = await users.CreateAsync(input.UserName, input.Password, ct);
            if (User.Identity?.IsAuthenticated != true) await SignInAsync(account, ct);
            return Ok(new { userName = account.UserName, role = account.Role });
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginInput input, CancellationToken ct)
    {
        if (input.Password is null || input.Password.Length > 256
            || !await users.VerifyAsync(input.UserName ?? "", input.Password, ct)) return Unauthorized(new { error = "Identifiants incorrects." });
        var account = await users.FindAsync(input.UserName!, ct);
        await SignInAsync(account!, ct);
        return Ok(new { userName = account!.UserName, role = account.Role });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return NoContent(); }

    private async Task SignInAsync(UserAccount user, CancellationToken ct)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.UserName), new Claim(ClaimTypes.Role, user.Role)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    }
    public sealed record CreateUserInput(string UserName, string Password);
    public sealed record LoginInput(string? UserName, string? Password);
}
