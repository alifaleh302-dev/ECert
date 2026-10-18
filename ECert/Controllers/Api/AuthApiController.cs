using System.Security.Claims;
using ECert.Data;
using ECert.Models.ApiModels;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers.Api;

[ApiController]
[Route("api/auth")]
public class AuthApiController : ControllerBase
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly LoginAttemptGuard _loginGuard;
    public AuthApiController(ECertDbContext db, AuditLogService audit, LoginAttemptGuard loginGuard)
    {
        _db = db;
        _audit = audit;
        _loginGuard = loginGuard;
    }

    [HttpPost("login")]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("admin-login")]
    public async Task<IActionResult> Login([FromBody] LoginViewModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ApiResponse.Fail("البيانات المدخلة غير صحيحة"));

        var username = model.Username.Trim();
        var clientAddress = GetClientAddress();
        var accountDelayed = _loginGuard.IsAccountDelayed(username, out var accountDelay);
        var addressDelayed = _loginGuard.IsDelayed(username, clientAddress, out var addressDelay);
        if (accountDelayed || addressDelayed)
            await Task.Delay(accountDelayed ? accountDelay : addressDelay, HttpContext.RequestAborted);

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Username == username && u.IsActive);

        if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
        {
            _loginGuard.RegisterFailure(username, clientAddress);
            _loginGuard.RegisterAccountFailure(username);
            return Ok(ApiResponse.Fail("اسم المستخدم أو كلمة المرور غير صحيحة"));
        }

        _loginGuard.RegisterSuccess(username, clientAddress);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Email, user.Email)
        };

        var permissions = new List<string>();
        foreach (var ur in user.UserRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, ur.Role!.RoleName));
            foreach (var rp in ur.Role.RolePermissions)
            {
                claims.Add(new Claim("Permission", rp.Permission!.PermissionName));
                permissions.Add(rp.Permission.PermissionName);
            }
        }

        if (user.MustChangePassword)
            claims.Add(new Claim("MustChangePassword", "true"));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        user.LastLoginAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(user.FullName, "Login", "User", user.UserId, ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        var roleName = user.UserRoles.FirstOrDefault()?.Role?.RoleName ?? "Admin";
        return Ok(ApiResponse<object>.Ok(new
        {
            userId = user.UserId,
            fullName = user.FullName,
            username = user.Username,
            email = user.Email,
            role = roleName,
            permissions,
            mustChangePassword = user.MustChangePassword
        }, "تم تسجيل الدخول بنجاح"));
    }

    private string GetClientAddress() => ClientAddressResolver.Resolve(HttpContext);

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        var user = new
        {
            name = User.Identity?.Name,
            id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            email = User.FindFirst(ClaimTypes.Email)?.Value,
            roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
            permissions = User.FindAll("Permission").Select(c => c.Value).ToList()
        };
        return Ok(ApiResponse<object>.Ok(user));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(ApiResponse.Ok("تم تسجيل الخروج بنجاح"));
    }
}
