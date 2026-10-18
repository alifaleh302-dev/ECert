using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class CategoriesManageController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;
    public CategoriesManageController(ECertDbContext db, AuditLogService audit, IWebHostEnvironment env)
    { _db = db; _audit = audit; _env = env; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    public async Task<IActionResult> Index(string? search)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var query = _db.Categories.Include(c => c.Courses).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(c => c.CategoryName.Contains(search));
        ViewBag.Search = search;
        var categories = await query.OrderBy(c => c.CategoryName).ToListAsync();
        return View(categories);
    }

    [HttpGet]
    public IActionResult Create()
    {
        if (!HasPermission("manage-courses")) return Forbid();
        PrepareIconOptions();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("CategoryName,Description,IsActive")] Category category, IFormFile? icon, string? presetIcon)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        if (string.IsNullOrWhiteSpace(category.CategoryName))
        {
            TempData["Error"] = "اسم الفئة مطلوب.";
            PrepareIconOptions();
            return View(category);
        }

        if (!TryApplyPresetIcon(category, presetIcon))
        {
            TempData["Error"] = "الأيقونة الجاهزة المختارة غير صالحة.";
            PrepareIconOptions();
            return View(category);
        }

        // الصورة المرفوعة تتقدم على الأيقونة الجاهزة عند اختيار الخيارين معاً.
        if (icon != null && icon.Length > 0)
        {
            var (iconPath, iconError) = await SaveIconAsync(icon);
            if (iconError != null)
            {
                ModelState.AddModelError(string.Empty, iconError);
                PrepareIconOptions();
                return View(category);
            }
            category.IconUrl = iconPath;
        }

        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Category", category.CategoryId, null, category.CategoryName);
        TempData["Success"] = "تمت إضافة الفئة بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var category = await _db.Categories.FindAsync(id);
        if (category == null) return NotFound();
        PrepareIconOptions();
        return View(category);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("CategoryName,Description,IsActive")] Category category, IFormFile? icon, string? presetIcon)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var existing = await _db.Categories.FindAsync(id);
        if (existing == null) return NotFound();

        if (string.IsNullOrWhiteSpace(category.CategoryName))
        {
            TempData["Error"] = "اسم الفئة مطلوب.";
            category.IconUrl = existing.IconUrl;
            PrepareIconOptions();
            return View(category);
        }

        if (!TryApplyPresetIcon(existing, presetIcon))
        {
            TempData["Error"] = "الأيقونة الجاهزة المختارة غير صالحة.";
            category.IconUrl = existing.IconUrl;
            PrepareIconOptions();
            return View(category);
        }

        existing.CategoryName = category.CategoryName;
        existing.Description = category.Description;
        existing.IsActive = category.IsActive;

        // الصورة المرفوعة تتقدم على الأيقونة الجاهزة عند اختيار الخيارين معاً.
        if (icon != null && icon.Length > 0)
        {
            var (iconPath, iconError) = await SaveIconAsync(icon);
            if (iconError != null)
            {
                ModelState.AddModelError(string.Empty, iconError);
                PrepareIconOptions();
                return View(existing);
            }
            existing.IconUrl = iconPath;
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "Category", id, null, category.CategoryName);
        TempData["Success"] = "تم تعديل الفئة بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var category = await _db.Categories.Include(c => c.Courses).FirstOrDefaultAsync(c => c.CategoryId == id);
        if (category == null) return NotFound();

        if (category.Courses.Any())
        {
            TempData["Error"] = "لا يمكن حذف فئة تحتوي على دورات.";
            return RedirectToAction("Index");
        }

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Delete", "Category", id);
        TempData["Success"] = "تم حذف الفئة.";
        return RedirectToAction("Index");
    }

    private void PrepareIconOptions() => ViewBag.PresetIcons = CategoryIconCatalog.Options;

    private static bool TryApplyPresetIcon(Category category, string? presetIcon)
    {
        if (string.IsNullOrWhiteSpace(presetIcon)) return true;
        if (!CategoryIconCatalog.IsAllowed(presetIcon)) return false;

        category.IconUrl = presetIcon;
        return true;
    }

    private async Task<(string? Path, string? Error)> SaveIconAsync(IFormFile icon)
    {
        var (valid, error) = await ImageUploadValidator.ValidateAsync(icon);
        if (!valid) return (null, error);

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "categories");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(icon.FileName)}";
        var filePath = Path.Combine(uploadsDir, fileName);
        using var stream = new FileStream(filePath, FileMode.Create);
        await icon.CopyToAsync(stream);
        return ($"/uploads/categories/{fileName}", null);
    }
}
