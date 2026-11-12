using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

public class AuthController : Controller
{
    private const string PendingTwoFactorCookie = "ecert_2fa";

    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly LoginThrottleService _throttle;
    private readonly LoginOtpService _otp;
    private readonly TelegramBotService _telegram;
    private readonly SecuritySettingsService _settings;
    private readonly SecurityAlertService _alerts;
    private readonly IDataProtector _protector;

    public AuthController(
        ECertDbContext db,
        AuditLogService audit,
        LoginThrottleService throttle,
        LoginOtpService otp,
        TelegramBotService telegram,
        SecuritySettingsService settings,
        SecurityAlertService alerts,
        IDataProtectionProvider dataProtection)
    {
        _db = db;
        _audit = audit;
        _throttle = throttle;
        _otp = otp;
        _telegram = telegram;
        _settings = settings;
        _alerts = alerts;
        _protector = dataProtection.CreateProtector("ECert.Auth.TwoFactor");
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

        // A blocked account or address is rejected before the password is even checked.
        if (_throttle.IsBlocked(username, clientAddress, out var blockedFor))
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, blockedFor.TotalSeconds)), HttpContext.RequestAborted);
            ModelState.AddModelError("", $"تم حظر المحاولات مؤقتاً. أعد المحاولة بعد {Math.Ceiling(blockedFor.TotalMinutes)} دقيقة.");
            return View(model);
        }

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Username == model.Username && u.IsActive);

        if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
        {
            _throttle.RegisterFailure(username, clientAddress);
            await _alerts.AlertRepeatedFailedLoginsAsync(username, clientAddress,
                SecurityPolicySnapshot.MaxFailedLoginAttempts);
            ModelState.AddModelError("", "اسم المستخدم أو كلمة المرور غير صحيحة");
            return View(model);
        }

        // Second factor: when enabled globally and the account is linked to Telegram, hold the
        // sign-in until an OTP sent to that chat is confirmed.
        var settings = await _settings.GetAsync();
        if (settings.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TelegramChatId))
        {
            var code = _otp.Issue(user.UserId);
            var sent = await _telegram.SendMessageAsync(settings.TelegramBotToken, user.TelegramChatId,
                $"رمز الدخول الخاص بك: <b>{code}</b>\nصالح لخمس دقائق. لا تشاركه مع أحد.");
            if (sent)
            {
                SetPendingTwoFactorCookie(user.UserId);
                return RedirectToAction(nameof(VerifyTwoFactor));
            }

            ModelState.AddModelError("", "تعذّر إرسال رمز التحقق عبر تليجرام. حاول لاحقاً أو راجع مدير النظام.");
            return View(model);
        }

        await SignInUserAsync(user, clientAddress);
        return RedirectAfterLogin(user);
    }

    [HttpGet]
    public IActionResult VerifyTwoFactor()
    {
        if (TryGetPendingUserId(out _) == false)
            return RedirectToAction(nameof(Login));
        return View(new TwoFactorViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("admin-login")]
    public async Task<IActionResult> VerifyTwoFactor(TwoFactorViewModel model)
    {
        if (!TryGetPendingUserId(out var userId))
            return RedirectToAction(nameof(Login));

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.UserId == userId && u.IsActive);
        if (user == null)
            return RedirectToAction(nameof(Login));

        if (!_otp.Verify(user.UserId, model.Code))
        {
            ModelState.AddModelError("", "رمز التحقق غير صحيح أو منتهي الصلاحية.");
            return View(model);
        }

        ClearPendingTwoFactorCookie();
        await SignInUserAsync(user, ClientAddressResolver.Resolve(HttpContext));
        return RedirectAfterLogin(user);
    }

    /// <summary>Issues the authentication cookie and records the successful sign-in.</summary>
    private async Task SignInUserAsync(User user, string clientAddress)
    {
        _throttle.RegisterSuccess(user.Username, clientAddress);

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

        var isSuperAdmin = user.UserRoles.Any(ur => ur.Role!.RoleName == AdminPrincipalExtensions.SuperAdminRole);
        await CourseScope.AddScopeClaimsAsync(claims, _db, user, isSuperAdmin);

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        user.LastLoginAt = DateTime.Now;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user.FullName, "Login", "User", user.UserId, ip: HttpContext.Connection.RemoteIpAddress?.ToString());
    }

    private IActionResult RedirectAfterLogin(User user)
    {
        if (user.MustChangePassword)
            return RedirectToAction("ChangePassword");

        var hasAdminPermission = user.UserRoles.Any(userRole => userRole.Role!.RolePermissions.Count > 0);
        return hasAdminPermission
            ? RedirectToAction("Index", "Dashboard")
            : RedirectToAction("Index", "Home");
    }

    private void SetPendingTwoFactorCookie(int userId)
    {
        var ticket = _protector.Protect($"{userId}|{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}");
        Response.Cookies.Append(PendingTwoFactorCookie, ticket, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !HttpContext.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase),
            MaxAge = TimeSpan.FromMinutes(5)
        });
    }

    private void ClearPendingTwoFactorCookie() => Response.Cookies.Delete(PendingTwoFactorCookie);

    private bool TryGetPendingUserId(out int userId)
    {
        userId = 0;
        if (!Request.Cookies.TryGetValue(PendingTwoFactorCookie, out var ticket) || string.IsNullOrEmpty(ticket))
            return false;

        try
        {
            var parts = _protector.Unprotect(ticket).Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[0], out userId))
                return false;
            if (!long.TryParse(parts[1], out var expiry) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
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
        return RedirectToAction("Login", "Auth");
    }
}
