using System.Security.Claims;
using ECert.Data;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

public class AuthController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly LoginAttemptGuard _loginGuard;
    public AuthController(ECertDbContext db, AuditLogService audit, LoginAttemptGuard loginGuard)
    {
        _db = db;
        _audit = audit;
        _loginGuard = loginGuard;
    }

    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("admin-login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var username = model.Username.Trim();
        var clientAddress = ClientAddressResolver.Resolve(HttpContext);
        var accountDelayed = _loginGuard.IsAccountDelayed(username, out var accountDelay);
        var addressDelayed = _loginGuard.IsDelayed(username, clientAddress, out var addressDelay);
        if (accountDelayed || addressDelayed)
            await Task.Delay(accountDelayed ? accountDelay : addressDelay, HttpContext.RequestAborted);

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Username == model.Username && u.IsActive);

        if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
        {
            _loginGuard.RegisterFailure(username, clientAddress);
            _loginGuard.RegisterAccountFailure(username);
            ModelState.AddModelError("", "اسم المستخدم أو كلمة المرور غير صحيحة");
            return View(model);
        }

        _loginGuard.RegisterSuccess(username, clientAddress);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Email, user.Email)
        };

        if (user.MustChangePassword)
            claims.Add(new Claim("MustChangePassword", "true"));

        foreach (var ur in user.UserRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, ur.Role!.RoleName));
            foreach (var rp in ur.Role.RolePermissions)
                claims.Add(new Claim("Permission", rp.Permission!.PermissionName));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        user.LastLoginAt = DateTime.Now;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user.FullName, "Login", "User", user.UserId, ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        if (user.MustChangePassword)
            return RedirectToAction("ChangePassword");

        // Redirect based on role
        var roleName = user.UserRoles.FirstOrDefault()?.Role?.RoleName ?? "Admin";
        return roleName switch
        {
            "SuperAdmin" => RedirectToAction("Index", "Dashboard"),
            "Admin" => RedirectToAction("Index", "ManagerDashboard"),
            "Media" => RedirectToAction("Index", "MediaDashboard"),
            "Finance" => RedirectToAction("Index", "FinanceDashboard"),
            _ => RedirectToAction("Index", "Dashboard")
        };
    }

    [Authorize]
    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
            return RedirectToAction("Login");

        var user = await _db.Users.FindAsync(userId);
        if (user == null) return RedirectToAction("Login");

        if (!BCrypt.Net.BCrypt.Verify(model.CurrentPassword, user.PasswordHash))
            ModelState.AddModelError(nameof(model.CurrentPassword), "كلمة المرور الحالية غير صحيحة.");

        var policyError = PasswordPolicy.Validate(model.NewPassword).FirstOrDefault();
        if (policyError != null)
            ModelState.AddModelError(nameof(model.NewPassword), policyError.ErrorMessage ?? "كلمة المرور لا تستوفي الشروط.");

        if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
            ModelState.AddModelError(nameof(model.ConfirmPassword), "كلمتا المرور غير متطابقتين.");

        if (!ModelState.IsValid)
            return View(model);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.NewPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user.FullName, "Update", "User", user.UserId, ip: ClientAddressResolver.Resolve(HttpContext));
        TempData["Success"] = "تم تحديث كلمة المرور بنجاح.";

        // Reissue the cookie so the forced-change claim is dropped.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }

    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }
}
