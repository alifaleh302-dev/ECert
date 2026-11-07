using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class RolesController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;

    public RolesController(ECertDbContext db, AuditLogService audit)
    {
        _db = db;
        _audit = audit;
    }

    private bool CanManageRoles => User.HasPermission(AdminPermissions.ManageRoles);

    private IActionResult? RequireRoleManager()
        => CanManageRoles ? null : Forbid();

    public async Task<IActionResult> Index()
    {
        if (RequireRoleManager() is { } denied) return denied;

        var roles = await _db.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .Include(r => r.UserRoles)
            .OrderByDescending(r => r.IsSystem)
            .ThenBy(r => r.RoleName)
            .ToListAsync();

        return View(roles);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (RequireRoleManager() is { } denied) return denied;
        await PopulatePermissionsAsync();
        return View(new RoleFormViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RoleFormViewModel model)
    {
        if (RequireRoleManager() is { } denied) return denied;

        var permissionIds = await ValidateRoleFormAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulatePermissionsAsync();
            return View(model);
        }

        var role = new Role
        {
            RoleName = model.RoleName.Trim(),
            Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(),
            IsSystem = false,
            CreatedAt = DateTime.Now
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        _db.RolePermissions.AddRange(permissionIds.Select(permissionId => new RolePermission
        {
            RoleId = role.RoleId,
            PermissionId = permissionId
        }));
        await _db.SaveChangesAsync();

        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Create", "Role", role.RoleId, null,
            $"Role: {role.RoleName}; Permissions: {string.Join(',', permissionIds)}");

        TempData["Success"] = "تم إنشاء الدور المخصص وربط صلاحياته بنجاح.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (RequireRoleManager() is { } denied) return denied;

        var role = await _db.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.RoleId == id);
        if (role == null) return NotFound();
        if (role.IsSystem)
        {
            TempData["Error"] = "لا يمكن تعديل أدوار النظام المحمية من هذه الصفحة.";
            return RedirectToAction(nameof(Index));
        }

        await PopulatePermissionsAsync();
        return View(new RoleFormViewModel
        {
            RoleName = role.RoleName,
            Description = role.Description,
            PermissionIds = role.RolePermissions.Select(rp => rp.PermissionId).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, RoleFormViewModel model)
    {
        if (RequireRoleManager() is { } denied) return denied;

        var role = await _db.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.RoleId == id);
        if (role == null) return NotFound();
        if (role.IsSystem) return Forbid();

        var permissionIds = await ValidateRoleFormAsync(model, id);
        if (!ModelState.IsValid)
        {
            await PopulatePermissionsAsync();
            return View(model);
        }

        role.RoleName = model.RoleName.Trim();
        role.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();

        _db.RolePermissions.RemoveRange(role.RolePermissions);
        _db.RolePermissions.AddRange(permissionIds.Select(permissionId => new RolePermission
        {
            RoleId = role.RoleId,
            PermissionId = permissionId
        }));
        await _db.SaveChangesAsync();

        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Update", "Role", role.RoleId, null,
            $"Role: {role.RoleName}; Permissions: {string.Join(',', permissionIds)}");

        TempData["Success"] = "تم تحديث الدور وصلاحياته. تسري الصلاحيات الجديدة عند تسجيل دخول المستخدمين المرتبطين به مرة أخرى.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (RequireRoleManager() is { } denied) return denied;

        var role = await _db.Roles
            .Include(r => r.UserRoles)
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.RoleId == id);
        if (role == null) return NotFound();
        if (role.IsSystem)
        {
            TempData["Error"] = "لا يمكن حذف أدوار النظام المحمية.";
            return RedirectToAction(nameof(Index));
        }
        if (role.UserRoles.Any())
        {
            TempData["Error"] = "لا يمكن حذف دور مرتبط بمستخدمين. انقل المستخدمين إلى دور آخر أولاً.";
            return RedirectToAction(nameof(Index));
        }

        _db.RolePermissions.RemoveRange(role.RolePermissions);
        _db.Roles.Remove(role);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Delete", "Role", id, null, $"Role: {role.RoleName}");
        TempData["Success"] = "تم حذف الدور المخصص.";
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulatePermissionsAsync()
    {
        // Filtered in memory: EF cannot translate IReadOnlySet.Contains, and the set is small.
        var permissions = await _db.Permissions.ToListAsync();
        ViewBag.Permissions = permissions
            .Where(p => !AdminPermissions.SuperAdminOnly.Contains(p.PermissionName))
            .OrderBy(p => p.PermissionName)
            .ToList();
    }

    private async Task<List<int>> ValidateRoleFormAsync(RoleFormViewModel model, int? existingRoleId = null)
    {
        model.RoleName = (model.RoleName ?? string.Empty).Trim();
        model.Description = model.Description?.Trim();
        var permissionIds = (model.PermissionIds ?? new List<int>()).Distinct().ToList();
        model.PermissionIds = permissionIds;

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "اسم الدور مطلوب");
        }
        else if (await _db.Roles.AnyAsync(r => r.RoleId != existingRoleId && r.RoleName.ToUpper() == model.RoleName.ToUpper()))
        {
            ModelState.AddModelError(nameof(model.RoleName), "يوجد دور آخر بالاسم نفسه");
        }

        // SuperAdminOnly is filtered in memory (EF cannot translate IReadOnlySet.Contains).
        var offeredPermissionIds = (await _db.Permissions.ToListAsync())
            .Where(p => !AdminPermissions.SuperAdminOnly.Contains(p.PermissionName))
            .Select(p => p.PermissionId)
            .ToHashSet();
        var validPermissionIds = permissionIds.Where(id => offeredPermissionIds.Contains(id)).Distinct().ToList();

        if (!permissionIds.Any())
        {
            ModelState.AddModelError(nameof(model.PermissionIds), "اختر صلاحية واحدة على الأقل لهذا الدور");
        }
        else if (validPermissionIds.Count != permissionIds.Count)
        {
            ModelState.AddModelError(nameof(model.PermissionIds), "تحتوي الصلاحيات المختارة على قيمة غير مسموحة");
        }

        return validPermissionIds;
    }
}
