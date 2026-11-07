using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class NewsManageController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;
    public NewsManageController(ECertDbContext db, AuditLogService audit, IWebHostEnvironment env)
    {
        _db = db; _audit = audit; _env = env;
    }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    private async Task<string?> SaveImage(IFormFile? image)
    {
        if (image == null || image.Length == 0) return null;
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "news");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
        var filePath = Path.Combine(uploadsDir, fileName);
        using var stream = new FileStream(filePath, FileMode.Create);
        await image.CopyToAsync(stream);
        return $"/uploads/news/{fileName}";
    }

    public async Task<IActionResult> Index(string? search)
    {
        if (!HasPermission("manage-news")) return Forbid();
        var query = _db.Posts.Where(p => p.PostType == "News").AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => p.Title.Contains(search));
        ViewBag.Search = search;
        ViewBag.News = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
        return View();
    }

    [HttpGet]
    public IActionResult Create()
    {
        if (!HasPermission("manage-news")) return Forbid();
        return View(new Post { PostType = "News" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Post model, IFormFile? image)
    {
        if (!HasPermission("manage-news")) return Forbid();
        if (!ModelState.IsValid) return View(model);
        model.PostType = "News";
        model.Author = User.Identity?.Name ?? "";
        model.CreatedAt = DateTime.Now;
        if (model.Status == "Published") model.PublishedAt = DateTime.Now;
        var imgPath = await SaveImage(image);
        if (imgPath != null) model.ImageUrl = imgPath;
        _db.Posts.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "News", model.PostId);
        TempData["Success"] = "تمت إضافة الخبر بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!HasPermission("manage-news")) return Forbid();
        var news = await _db.Posts.FirstOrDefaultAsync(p => p.PostId == id && p.PostType == "News");
        if (news == null) return NotFound();
        return View(news);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Post model, IFormFile? image)
    {
        if (!HasPermission("manage-news")) return Forbid();
        if (!ModelState.IsValid) return View(model);
        var news = await _db.Posts.FirstOrDefaultAsync(p => p.PostId == model.PostId && p.PostType == "News");
        if (news == null) return NotFound();
        news.Title = model.Title;
        news.Content = model.Content;
        news.Status = model.Status;
        news.UpdatedAt = DateTime.Now;
        var imgPath = await SaveImage(image);
        if (imgPath != null)
        {
            // Delete old image if exists
            if (!string.IsNullOrEmpty(news.ImageUrl))
            {
                var oldImgPath = Path.Combine(_env.WebRootPath, news.ImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(oldImgPath))
                    System.IO.File.Delete(oldImgPath);
            }
            news.ImageUrl = imgPath;
        }
        if (model.Status == "Published" && news.PublishedAt == null) news.PublishedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Edit", "News", news.PostId);
        TempData["Success"] = "تم تعديل الخبر بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id)
    {
        if (!HasPermission("manage-news")) return Forbid();
        var news = await _db.Posts.FirstOrDefaultAsync(p => p.PostId == id && p.PostType == "News");
        if (news == null) return NotFound();
        news.Status = "Published";
        news.PublishedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم نشر الخبر.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish(int id)
    {
        if (!HasPermission("manage-news")) return Forbid();
        var news = await _db.Posts.FirstOrDefaultAsync(p => p.PostId == id && p.PostType == "News");
        if (news == null) return NotFound();
        news.Status = "Draft";
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم إلغاء نشر الخبر.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!HasPermission("manage-news")) return Forbid();
        var news = await _db.Posts.FirstOrDefaultAsync(p => p.PostId == id && p.PostType == "News");
        if (news == null) return NotFound();

        // Delete image file from disk
        if (!string.IsNullOrEmpty(news.ImageUrl))
        {
            var imgPath = Path.Combine(_env.WebRootPath, news.ImageUrl.TrimStart('/'));
            if (System.IO.File.Exists(imgPath))
                System.IO.File.Delete(imgPath);
        }

        _db.Posts.Remove(news);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Delete", "News", id);
        TempData["Success"] = "تم حذف الخبر.";
        return RedirectToAction("Index");
    }
}
