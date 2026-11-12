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
    private readonly TelegramLinkService _telegramLink;
    public UsersController(ECertDbContext db, AuditLogService audit, TelegramLinkService telegramLink)
    {
        _db = db;
        _audit = audit;
        _telegramLink = telegramLink;
    }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);
    private bool IsSuperAdmin => User.IsSuperAdmin();
    private IQueryable<Role> AssignableRolesQuery() => IsSuperAdmin ? _db.Roles : _db.Roles.Where(r => !r.IsSystem);

    /// <summary>
    /// The courses a SuperAdmin may hand to a user. A non-SuperAdmin never sees the picker: the
    /// accounts they create inherit their own course scope, so they can never widen access.
    /// </summary>
    private Task<List<Course>> SelectableCoursesAsync() =>
        _db.Courses.Include(c => c.Category)
            .OrderBy(c => c.CategoryId).ThenBy(c => c.CourseNameArabic ?? c.CourseName)
            .ToListAsync();

    private Task<List<Category>> SelectableCategoriesAsync() =>
        _db.Categories.OrderBy(c => c.CategoryName).ToListAsync();

    /// <summary>Builds the course-scope picker model shared by the create and edit screens.</summary>
    private async Task<CourseScopePickerViewModel> BuildScopePickerAsync(
        IEnumerable<int>? courseIds = null, IEnumerable<int>? categoryIds = null, bool isUnscopableAccount = false)
    {
        var canAssign = IsSuperAdmin && !isUnscopableAccount;
        return new CourseScopePickerViewModel
        {
            CanAssign = canAssign,
            IsUnscopableAccount = isUnscopableAccount,
            Categories = canAssign ? await SelectableCategoriesAsync() : new List<Category>(),
            Courses = canAssign ? await SelectableCoursesAsync() : new List<Course>(),
            SelectedCourseIds = (courseIds ?? Enumerable.Empty<int>()).ToList(),
            SelectedCategoryIds = (categoryIds ?? Enumerable.Empty<int>()).ToList()
        };
    }

    public async Task<IActionResult> Index(string? search, int? roleId, string? status, string? scope)
    {
        if (!HasPermission("manage-users")) return Forbid();
        var query = _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.CourseAssignments).ThenInclude(uc => uc.Course)
            .Include(u => u.CategoryAssignments).ThenInclude(uc => uc.Category)
            .AsSplitQuery()
            .AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => u.FullName.Contains(search) || u.Username.Contains(search) || (u.Email != null && u.Email.Contains(search)));
        if (roleId.HasValue)
            query = query.Where(u => u.UserRoles.Any(ur => ur.RoleId == roleId.Value));
        if (status == "active")
            query = query.Where(u => u.IsActive);
        else if (status == "inactive")
            query = query.Where(u => !u.IsActive);
        // A scoped user has at least one course or category assignment; an unscoped one has none.
        if (scope == "scoped")
            query = query.Where(u => u.CourseAssignments.Any() || u.CategoryAssignments.Any());
        else if (scope == "all-courses")
            query = query.Where(u => !u.CourseAssignments.Any() && !u.CategoryAssignments.Any());
        ViewBag.Search = search;
        ViewBag.RoleId = roleId;
        ViewBag.Status = status;
        ViewBag.Scope = scope;
        ViewBag.Roles = await _db.Roles.ToListAsync();
        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();
        return View(users);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (!HasPermission("manage-users")) return Forbid();
        await PopulateFormDataAsync();
        return View(new CreateUserViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserViewModel model)
    {
        if (!HasPermission("manage-users")) return Forbid();

        var assignableRoles = await AssignableRolesQuery().OrderBy(r => r.RoleName).ToListAsync();
        if (!ModelState.IsValid)
        {
            await PopulateFormDataAsync(assignableRoles, model.CourseIds, model.CategoryIds);
            return View(model);
        }
        if (!assignableRoles.Any(r => r.RoleId == model.RoleId))
        {
            ModelState.AddModelError(nameof(model.RoleId), "اختر دوراً متاحاً للمستخدم الجديد");
            await PopulateFormDataAsync(assignableRoles, model.CourseIds, model.CategoryIds);
            return View(model);
        }
        if (await _db.Users.AnyAsync(u => u.Username == model.Username))
        {
            ModelState.AddModelError("Username", "اسم المستخدم مستخدم مسبقاً");
            await PopulateFormDataAsync(assignableRoles, model.CourseIds, model.CategoryIds);
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

        // Course confinement: a SuperAdmin picks explicitly (courses and/or whole categories);
        // anyone else inherits the creator's effective scope, so a lesser administrator cannot
        // mint a more powerful account.
        if (IsSuperAdmin)
        {
            var courseIds = await ValidateCourseIdsAsync(model.CourseIds);
            foreach (var courseId in courseIds)
                _db.UserCourses.Add(new UserCourse { UserId = user.UserId, CourseId = courseId });

            var categoryIds = await ValidateCategoryIdsAsync(model.CategoryIds);
            foreach (var categoryId in categoryIds)
                _db.UserCategories.Add(new UserCategory { UserId = user.UserId, CategoryId = categoryId });

            await _db.SaveChangesAsync();
            await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "User", user.UserId, null,
                $"User: {model.Username}; Courses: {(courseIds.Count == 0 ? "none" : string.Join(',', courseIds))}; " +
                $"Categories: {(categoryIds.Count == 0 ? "none" : string.Join(',', categoryIds))}");
        }
        else
        {
            var inheritedCourseIds = User.AllowedCourseIds()?.ToList() ?? new List<int>();
            foreach (var courseId in inheritedCourseIds)
                _db.UserCourses.Add(new UserCourse { UserId = user.UserId, CourseId = courseId });

            await _db.SaveChangesAsync();
            await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "User", user.UserId, null,
                $"User: {model.Username}; Courses: {(inheritedCourseIds.Count == 0 ? "all" : string.Join(',', inheritedCourseIds))}");
        }

        TempData["Success"] = "تم إنشاء المستخدم بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!HasPermission("manage-users")) return Forbid();
        var user = await _db.Users
            .Include(u => u.UserRoles)
            .Include(u => u.CourseAssignments)
            .Include(u => u.CategoryAssignments)
            .FirstOrDefaultAsync(u => u.UserId == id);
        if (user == null) return NotFound();

        var isSuperAdminAccount = await IsSuperAdminAccountAsync(user);
        var courseIds = user.CourseAssignments.Select(uc => uc.CourseId).ToList();
        var categoryIds = user.CategoryAssignments.Select(uc => uc.CategoryId).ToList();
        await PopulateFormDataAsync(null, courseIds, categoryIds, isSuperAdminAccount);

        ViewBag.TelegramChatId = user.TelegramChatId;
        ViewBag.TelegramLinkCode = user.TelegramLinkCode;
        ViewBag.TelegramLinkedAt = user.TelegramLinkedAt;

        return View(new EditUserViewModel
        {
            UserId = user.UserId,
            Username = user.Username,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            RoleId = user.UserRoles.FirstOrDefault()?.RoleId ?? 0,
            IsActive = user.IsActive,
            CourseIds = courseIds,
            CategoryIds = categoryIds
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, EditUserViewModel model)
    {
        if (!HasPermission("manage-users")) return Forbid();
        var user = await _db.Users
            .Include(u => u.UserRoles)
            .Include(u => u.CourseAssignments)
            .Include(u => u.CategoryAssignments)
            .FirstOrDefaultAsync(u => u.UserId == id);
        if (user == null) return NotFound();

        var isSuperAdminAccount = await IsSuperAdminAccountAsync(user);
        var assignableRoles = await AssignableRolesQuery().OrderBy(r => r.RoleName).ToListAsync();
        if (!assignableRoles.Any(r => r.RoleId == model.RoleId))
            ModelState.AddModelError(nameof(model.RoleId), "اختر دوراً متاحاً");

        if (!ModelState.IsValid)
        {
            await PopulateFormDataAsync(assignableRoles, model.CourseIds, model.CategoryIds, isSuperAdminAccount);
            return View(model);
        }

        user.FullName = model.FullName;
        user.Email = model.Email;
        user.Phone = model.Phone;
        user.IsActive = model.IsActive;

        // Replace the single role binding.
        var currentRoleId = user.UserRoles.FirstOrDefault()?.RoleId;
        if (currentRoleId != model.RoleId)
        {
            _db.UserRoles.RemoveRange(user.UserRoles);
            _db.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = model.RoleId });
        }

        // Only a SuperAdmin may (re)assign scope, and never on the SuperAdmin account itself. For
        // everyone else the existing scope is kept untouched, so a lesser administrator cannot
        // widen a user's access.
        if (IsSuperAdmin && !isSuperAdminAccount)
        {
            var courseIds = await ValidateCourseIdsAsync(model.CourseIds);
            _db.UserCourses.RemoveRange(user.CourseAssignments);
            foreach (var courseId in courseIds)
                _db.UserCourses.Add(new UserCourse { UserId = user.UserId, CourseId = courseId });

            var categoryIds = await ValidateCategoryIdsAsync(model.CategoryIds);
            _db.UserCategories.RemoveRange(user.CategoryAssignments);
            foreach (var categoryId in categoryIds)
                _db.UserCategories.Add(new UserCategory { UserId = user.UserId, CategoryId = categoryId });
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "User", id, null, $"User: {user.Username}");
        TempData["Success"] = "تم تحديث بيانات المستخدم.";
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateTelegramLink(int id)
    {
        if (!HasPermission("manage-users")) return Forbid();
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        var code = await _telegramLink.GenerateCodeAsync(id);
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "User", id, null, "Telegram link code issued");
        TempData["Success"] = $"رمز الربط للمستخدم {user.Username}: {code} — يرسله الموظف إلى البوت لربط حسابه.";
        return RedirectToAction("Edit", new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkTelegram(int id)
    {
        if (!HasPermission("manage-users")) return Forbid();
        await _telegramLink.UnlinkAsync(id);
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "User", id, null, "Telegram unlinked");
        TempData["Success"] = "تم إلغاء ربط تليجرام.";
        return RedirectToAction("Edit", new { id });
    }

    private async Task PopulateFormDataAsync(
        List<Role>? roles = null,
        IEnumerable<int>? courseIds = null,
        IEnumerable<int>? categoryIds = null,
        bool isUnscopableAccount = false)
    {
        ViewBag.Roles = roles ?? await AssignableRolesQuery().OrderBy(r => r.RoleName).ToListAsync();
        ViewBag.Scope = await BuildScopePickerAsync(courseIds, categoryIds, isUnscopableAccount);
    }

    private async Task<bool> IsSuperAdminAccountAsync(User user)
    {
        var roleIds = user.UserRoles.Select(ur => ur.RoleId).ToList();
        return await _db.Roles.AnyAsync(r => roleIds.Contains(r.RoleId) && r.RoleName == AdminPrincipalExtensions.SuperAdminRole);
    }

    /// <summary>Keeps only ids that refer to a real course, so a tampered form cannot insert a dangling row.</summary>
    private async Task<List<int>> ValidateCourseIdsAsync(IEnumerable<int>? courseIds)
    {
        var requested = (courseIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
        if (requested.Count == 0) return new List<int>();
        return await _db.Courses.Where(c => requested.Contains(c.CourseId)).Select(c => c.CourseId).ToListAsync();
    }

    /// <summary>Keeps only ids that refer to a real category, so a tampered form cannot insert a dangling row.</summary>
    private async Task<List<int>> ValidateCategoryIdsAsync(IEnumerable<int>? categoryIds)
    {
        var requested = (categoryIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
        if (requested.Count == 0) return new List<int>();
        return await _db.Categories.Where(c => requested.Contains(c.CategoryId)).Select(c => c.CategoryId).ToListAsync();
    }
}
