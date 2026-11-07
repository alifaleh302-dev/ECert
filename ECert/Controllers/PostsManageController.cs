using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class PostsManageController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;
    public PostsManageController(ECertDbContext db, AuditLogService audit, IWebHostEnvironment env)
    { _db = db; _audit = audit; _env = env; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    public async Task<IActionResult> Index(string? search)
    {
        if (!HasPermission("manage-posts")) return Forbid();
        var query = _db.Posts.AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => p.Title.Contains(search));
        ViewBag.Search = search;
        var posts = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
        return View(posts);
    }

    [HttpGet]
    public IActionResult Create()
    {
        if (!HasPermission("manage-posts")) return Forbid();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Post post, IFormFile? image)
    {
        if (!HasPermission("manage-posts")) return Forbid();
        if (!ModelState.IsValid) return View(post);
        post.Author = User.Identity?.Name ?? "Unknown";
        post.CreatedAt = DateTime.Now;
        if (post.Status == "Published") post.PublishedAt = DateTime.Now;

        if (image != null && image.Length > 0)
            post.ImageUrl = await SaveImage(image);

        _db.Posts.Add(post);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Post", post.PostId, null, post.Title);
        TempData["Success"] = "تم إنشاء المنشور بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!HasPermission("manage-posts")) return Forbid();
        var post = await _db.Posts.FindAsync(id);
        if (post == null) return NotFound();
        return View(post);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Post post, IFormFile? image)
    {
        if (!HasPermission("manage-posts")) return Forbid();
        if (!ModelState.IsValid) return View(post);
        var existing = await _db.Posts.FindAsync(post.PostId);
        if (existing == null) return NotFound();
        existing.Title = post.Title;
        existing.Content = post.Content;
        existing.Status = post.Status;
        existing.UpdatedAt = DateTime.Now;
        if (post.Status == "Published" && existing.PublishedAt == null) existing.PublishedAt = DateTime.Now;

        if (image != null && image.Length > 0)
            existing.ImageUrl = await SaveImage(image);

        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "Post", post.PostId, null, post.Title);
        TempData["Success"] = "تم تعديل المنشور بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id)
    {
        if (!HasPermission("manage-posts")) return Forbid();
        var post = await _db.Posts.FindAsync(id);
        if (post == null) return NotFound();
        post.Status = "Published";
        post.PublishedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم نشر المنشور.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish(int id)
    {
        if (!HasPermission("manage-posts")) return Forbid();
        var post = await _db.Posts.FindAsync(id);
        if (post == null) return NotFound();
        post.Status = "Draft";
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم إلغاء نشر المنشور.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!HasPermission("manage-posts")) return Forbid();
        var post = await _db.Posts.FindAsync(id);
        if (post == null) return NotFound();
        _db.Posts.Remove(post);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Delete", "Post", id, null, post.Title);
        TempData["Success"] = "تم حذف المنشور.";
        return RedirectToAction("Index");
    }

    private async Task<string> SaveImage(IFormFile image)
    {
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "posts");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
        var filePath = Path.Combine(uploadsDir, fileName);
        using var stream = new FileStream(filePath, FileMode.Create);
        await image.CopyToAsync(stream);
        return $"/uploads/posts/{fileName}";
    }
}
