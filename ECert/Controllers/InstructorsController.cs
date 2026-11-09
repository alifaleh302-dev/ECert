using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class InstructorsController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;
    public InstructorsController(ECertDbContext db, AuditLogService audit, IWebHostEnvironment env)
    {
        _db = db; _audit = audit; _env = env;
    }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    private async Task<string?> SavePhoto(IFormFile? photo)
    {
        if (photo == null || photo.Length == 0) return null;
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "instructors");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(photo.FileName)}";
        var filePath = Path.Combine(uploadsDir, fileName);
        using var stream = new FileStream(filePath, FileMode.Create);
        await photo.CopyToAsync(stream);
        return $"/uploads/instructors/{fileName}";
    }

    /// <summary>
    /// Stores a signature captured on screen (PNG). Validation reuses the shared image guard so a
    /// crafted upload cannot smuggle a non-image payload through the signature field.
    /// </summary>
    private async Task<(string? Path, string? Error)> SaveSignatureAsync(IFormFile? signature)
    {
        if (signature == null || signature.Length == 0) return (null, null);

        var (valid, error) = await ImageUploadValidator.ValidateAsync(signature);
        if (!valid) return (null, error);

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "signatures");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}.png";
        using var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.Create);
        await signature.CopyToAsync(stream);
        return ($"/uploads/signatures/{fileName}", null);
    }

    private void DeleteSignature(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("/uploads/signatures/", StringComparison.Ordinal))
            return;

        var path = Path.Combine(_env.WebRootPath, url.TrimStart('/'));
        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);
    }

    public async Task<IActionResult> Index(string? search)
    {
        if (!HasPermission("manage-instructors")) return Forbid();
        var query = _db.Instructors.Include(i => i.CourseInstructors).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(i => i.FullName.Contains(search) || i.Specialization!.Contains(search));
        ViewBag.Search = search;
        var instructors = await query.OrderByDescending(i => i.CreatedAt).ToListAsync();
        return View(instructors);
    }

    [HttpGet]
    public IActionResult Create()
    {
        if (!HasPermission("manage-instructors")) return Forbid();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Instructor instructor, IFormFile? photo, IFormFile? signature)
    {
        if (!HasPermission("manage-instructors")) return Forbid();
        if (!ModelState.IsValid) return View(instructor);
        instructor.CreatedAt = DateTime.Now;
        var photoPath = await SavePhoto(photo);
        if (photoPath != null) instructor.PhotoUrl = photoPath;

        var (sigPath, sigError) = await SaveSignatureAsync(signature);
        if (sigError != null)
        {
            ModelState.AddModelError(string.Empty, sigError);
            return View(instructor);
        }
        if (sigPath != null) instructor.SignatureImageUrl = sigPath;

        _db.Instructors.Add(instructor);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Instructor", instructor.InstructorId, null, instructor.FullName);
        TempData["Success"] = "تمت إضافة المدرب بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!HasPermission("manage-instructors")) return Forbid();
        var instructor = await _db.Instructors.FindAsync(id);
        if (instructor == null) return NotFound();
        return View(instructor);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Instructor instructor, IFormFile? photo, IFormFile? signature, bool removeSignature = false)
    {
        if (!HasPermission("manage-instructors")) return Forbid();
        if (!ModelState.IsValid) return View(instructor);
        var existing = await _db.Instructors.FindAsync(instructor.InstructorId);
        if (existing == null) return NotFound();
        existing.FullName = instructor.FullName;
        existing.Bio = instructor.Bio;
        existing.Specialization = instructor.Specialization;
        existing.Phone = instructor.Phone;
        existing.Email = instructor.Email;
        existing.IsActive = instructor.IsActive;
        var photoPath = await SavePhoto(photo);
        if (photoPath != null) existing.PhotoUrl = photoPath;

        var (sigPath, sigError) = await SaveSignatureAsync(signature);
        if (sigError != null)
        {
            ModelState.AddModelError(string.Empty, sigError);
            return View(existing);
        }
        if (sigPath != null)
        {
            DeleteSignature(existing.SignatureImageUrl);
            existing.SignatureImageUrl = sigPath;
        }
        else if (removeSignature)
        {
            DeleteSignature(existing.SignatureImageUrl);
            existing.SignatureImageUrl = null;
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "Instructor", instructor.InstructorId, null, instructor.FullName);
        TempData["Success"] = "تم تعديل بيانات المدرب بنجاح.";
        return RedirectToAction("Index");
    }
}
