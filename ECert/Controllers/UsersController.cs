using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class UsersController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    public UsersController(ECertDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);
    private bool IsSuperAdmin => User.IsInRole("SuperAdmin");
    private IQueryable<Role> AssignableRolesQuery() => IsSuperAdmin ? _db.Roles : _db.Roles.Where(r => !r.IsSystem);

    public async Task<IActionResult> Index(string? search, int? roleId, string? status)
    {
        if (!HasPermission("manage-users")) return Forbid();
        var query = _db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => u.FullName.Contains(search) || u.Username.Contains(search) || (u.Email != null && u.Email.Contains(search)));
        if (roleId.HasValue)
            query = query.Where(u => u.UserRoles.Any(ur => ur.RoleId == roleId.Value));
        if (status == "active")
            query = query.Where(u => u.IsActive);
        else if (status == "inactive")
            query = query.Where(u => !u.IsActive);
        ViewBag.Search = search;
        ViewBag.RoleId = roleId;
        ViewBag.Status = status;
        ViewBag.Roles = await _db.Roles.ToListAsync();
        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return View(users);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (!HasPermission("manage-users")) return Forbid();
        ViewBag.Roles = await AssignableRolesQuery().OrderBy(r => r.RoleName).ToListAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserViewModel model)
    {
        if (!HasPermission("manage-users")) return Forbid();

        var assignableRoles = await AssignableRolesQuery().OrderBy(r => r.RoleName).ToListAsync();
        if (!ModelState.IsValid)
        {
            ViewBag.Roles = assignableRoles;
            return View(model);
        }
        if (!assignableRoles.Any(r => r.RoleId == model.RoleId))
        {
            ModelState.AddModelError(nameof(model.RoleId), "اختر دوراً متاحاً للمستخدم الجديد");
            ViewBag.Roles = assignableRoles;
            return View(model);
        }
        if (await _db.Users.AnyAsync(u => u.Username == model.Username))
        {
            ModelState.AddModelError("Username", "اسم المستخدم مستخدم مسبقاً");
            ViewBag.Roles = assignableRoles;
            return View(model);
        }

        var user = new User
        {
            Username = model.Username,
            FullName = model.FullName,
            Email = model.Email,
            Phone = model.Phone,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password),
            IsActive = true,
            CreatedAt = DateTime.Now
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = model.RoleId });
        await _db.SaveChangesAsync();

        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "User", user.UserId, null, $"User: {model.Username}");
        TempData["Success"] = "تم إنشاء المستخدم بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id)
    {
        if (!HasPermission("manage-users")) return Forbid();
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();
        user.IsActive = !user.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "User", id, null, $"Active: {user.IsActive}");
        TempData["Success"] = user.IsActive ? "تم تفعيل المستخدم." : "تم تعطيل المستخدم.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id, string newPassword)
    {
        if (!HasPermission("manage-users")) return Forbid();
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        var policyError = PasswordPolicy.Validate(newPassword).FirstOrDefault();
        if (policyError != null)
        {
            TempData["Error"] = policyError.ErrorMessage;
            return RedirectToAction("Index");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.MustChangePassword = true;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "User", id, null, "Password reset");
        TempData["Success"] = "تم إعادة تعيين كلمة المرور.";
        return RedirectToAction("Index");
    }
}
