using System.Security.Claims;
using System.Text.Json;
using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class CertificateDesignsController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;

    public CertificateDesignsController(ECertDbContext db, AuditLogService audit, IWebHostEnvironment env)
    {
        _db = db;
        _audit = audit;
        _env = env;
    }

    /// <summary>
    /// Stores the uploaded template background under wwwroot/uploads/designs and returns its
    /// public path, or an error when the file is not a valid raster image.
    /// </summary>
    private async Task<(string? Path, string? Error)> SaveBackgroundImageAsync(IFormFile? image)
    {
        if (image == null || image.Length == 0) return (null, null);

        var (valid, error) = await ImageUploadValidator.ValidateAsync(image);
        if (!valid) return (null, error);

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "designs");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
        using var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.Create);
        await image.CopyToAsync(stream);
        return ($"/uploads/designs/{fileName}", null);
    }

    private void DeleteBackgroundImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("/uploads/designs/", StringComparison.Ordinal))
            return;

        var path = Path.Combine(_env.WebRootPath, url.TrimStart('/'));
        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);
    }

    /// <summary>
    /// Accepts an image for a static (non-dynamic) design element and returns its public path. The
    /// designer uploads the file, then places the returned URL on an <c>image</c> element.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadImage(IFormFile? file)
    {
        if (!await CanManageAsync()) return Forbid();

        var (valid, error) = await ImageUploadValidator.ValidateAsync(file!);
        if (!valid) return BadRequest(new { error });

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "designs");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file!.FileName)}";
        using (var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var url = $"{CertificateDesignService.UploadUrlPrefix}{fileName}";
        await _audit.LogAsync(
            User.Identity?.Name ?? string.Empty,
            "UploadImage",
            "CertificateDesign",
            null,
            null,
            $"Uploaded: {url}",
            HttpContext.Connection.RemoteIpAddress?.ToString());
        return Json(new { url });
    }

    /// <summary>
    /// Deletes a background file only when no other design still points at it. Duplicated and forked
    /// designs share the same file path, so deleting it on one of them would blank the others.
    /// </summary>
    private async Task DeleteBackgroundImageIfUnreferencedAsync(string? url, int exceptDesignId)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("/uploads/designs/", StringComparison.Ordinal))
            return;

        var stillReferenced = await _db.CertificateDesigns
            .AnyAsync(d => d.CertificateDesignId != exceptDesignId && d.BackgroundImageUrl == url);
        if (stillReferenced) return;

        DeleteBackgroundImage(url);
    }

    private async Task<bool> CanManageAsync()
    {
        if (User.IsInRole("SuperAdmin") || User.HasClaim(c => c.Type == "Permission" && c.Value == "manage-certificate-designs"))
            return true;

        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return false;

        return await _db.UserRoles
            .Where(userRole => userRole.UserId == userId)
            .AnyAsync(userRole => userRole.Role!.RolePermissions
                .Any(rolePermission => rolePermission.Permission!.PermissionName == "manage-certificate-designs"));
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? id)
    {
        if (!await CanManageAsync()) return Forbid();

        var designs = await _db.CertificateDesigns
            .Include(t => t.Elements)
            .OrderByDescending(t => t.IsPublished)
            .ThenByDescending(t => t.UpdatedAt)
            .ToListAsync();

        var selected = id.HasValue
            ? designs.FirstOrDefault(t => t.CertificateDesignId == id.Value)
            : designs.FirstOrDefault(t => t.IsPublished) ?? designs.FirstOrDefault();

        selected ??= CertificateDesignService.CreateDefault(User.Identity?.Name);
        ViewData["Title"] = "مصمم الشهادات";
        ViewData["PageIcon"] = "bi-palette";
        ViewData["PageDesc"] = "حرّك العناصر وأضف الحقول ثم احفظ التصميم";
        ViewData["FullWidth"] = true;
        return View(new CertificateDesignEditorViewModel
        {
            Design = selected,
            Designs = designs,
            FieldLabels = CertificateDesignService.FieldLabels
        });
    }

    [HttpGet]
    public async Task<IActionResult> New()
    {
        if (!await CanManageAsync()) return Forbid();

        var designs = await _db.CertificateDesigns
            .Include(t => t.Elements)
            .OrderByDescending(t => t.IsPublished)
            .ThenByDescending(t => t.UpdatedAt)
            .ToListAsync();

        ViewData["Title"] = "تصميم شهادة جديد";
        var draft = CertificateDesignService.CreateDefault(User.Identity?.Name);
        draft.Name = await NextDesignNameAsync();
        draft.IsPublished = false;
        draft.DesignKey = string.Empty;
        return View("Index", new CertificateDesignEditorViewModel
        {
            Design = draft,
            Designs = designs,
            FieldLabels = CertificateDesignService.FieldLabels
        });
    }

    /// <summary>
    /// Suggests a name that is not already taken ("قالب جديد 2", "قالب جديد 3", …) so freshly created
    /// templates are distinguishable in the list instead of all sharing one default name.
    /// </summary>
    private async Task<string> NextDesignNameAsync()
    {
        const string baseName = "قالب جديد";
        var names = await _db.CertificateDesigns
            .Where(t => t.Name == baseName || t.Name.StartsWith(baseName + " "))
            .Select(t => t.Name)
            .ToListAsync();

        if (!names.Contains(baseName)) return baseName;
        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!names.Contains(candidate)) return candidate;
        }
        return $"{baseName} {Guid.NewGuid():N}"[..20];
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CertificateDesignEditorPostModel model, IFormFile? backgroundImage)
    {
        if (!await CanManageAsync()) return Forbid();

        var canvasWidth = Math.Clamp(model.CanvasWidth, 800, 1600);
        var canvasHeight = Math.Clamp(model.CanvasHeight, 500, 1200);
        var elements = ParseElements(model.ElementsJson, canvasWidth, canvasHeight, out var parseError);
        if (parseError != null)
        {
            TempData["Error"] = parseError;
            return RedirectToAction(nameof(Index), new { id = model.CertificateDesignId > 0 ? model.CertificateDesignId : (int?)null });
        }

        var current = model.CertificateDesignId > 0
            ? await _db.CertificateDesigns.Include(t => t.Elements).FirstOrDefaultAsync(t => t.CertificateDesignId == model.CertificateDesignId)
            : null;

        if (model.CertificateDesignId > 0 && current == null)
            return NotFound();

        var (newBackground, backgroundError) = await SaveBackgroundImageAsync(backgroundImage);
        if (backgroundError != null)
        {
            TempData["Error"] = backgroundError;
            return RedirectToAction(nameof(Index), new { id = model.CertificateDesignId > 0 ? model.CertificateDesignId : (int?)null });
        }

        var saveAsDraft = !model.Publish && current?.IsPublished == true;
        if (saveAsDraft)
        {
            // Editing a published design without publishing keeps the published artwork intact by
            // forking it into a new draft copy.
            current = await CloneDesignAsync(current!, " (مسودة)");
        }

        CertificateDesign design;
        if (current != null)
        {
            design = current;
        }
        else
        {
            design = new CertificateDesign
            {
                Name = TrimTo(model.Name, 120, "تصميم شهادة"),
                DesignKey = $"design-{Guid.NewGuid():N}",
                CreatedAt = DateTime.Now
            };
            _db.CertificateDesigns.Add(design);
        }

        design.Name = TrimTo(model.Name, 120, "تصميم شهادة");
        design.CanvasWidth = canvasWidth;
        design.CanvasHeight = canvasHeight;
        design.BackgroundColor = CertificateDesignService.NormalizeColor(model.BackgroundColor, "#fffdf7");
        design.BorderColor = CertificateDesignService.NormalizeColor(model.BorderColor, "#c9a227");
        design.BorderWidth = Math.Clamp(model.BorderWidth, 0, 24);
        design.BorderRadius = Math.Clamp(model.BorderRadius, 0, 80);

        if (newBackground != null)
        {
            if (design.CertificateDesignId > 0)
                await DeleteBackgroundImageIfUnreferencedAsync(current?.BackgroundImageUrl, design.CertificateDesignId);
            design.BackgroundImageUrl = newBackground;
        }
        else if (model.RemoveBackgroundImage)
        {
            if (design.CertificateDesignId > 0)
                await DeleteBackgroundImageIfUnreferencedAsync(current?.BackgroundImageUrl, design.CertificateDesignId);
            design.BackgroundImageUrl = null;
        }
        else if (design.CertificateDesignId == 0)
        {
            // A brand-new draft copy inherits the published artwork so it still renders.
            design.BackgroundImageUrl = current?.BackgroundImageUrl;
        }

        design.UpdatedBy = TrimTo(User.Identity?.Name, 100, "system");
        design.UpdatedAt = DateTime.Now;
        // Publishing a design makes it available to certificates that have no course-specific
        // template; it does not affect the other published designs, which stay selectable.
        design.IsPublished = model.Publish;

        if (design.CertificateDesignId > 0 && design.Elements.Count > 0)
            _db.CertificateDesignElements.RemoveRange(design.Elements);

        foreach (var element in elements)
            design.Elements.Add(element);

        await _db.SaveChangesAsync();
        await _audit.LogAsync(
            User.Identity?.Name ?? string.Empty,
            model.Publish ? "Publish" : "SaveDraft",
            "CertificateDesign",
            design.CertificateDesignId,
            null,
            $"Design: {design.Name}; Elements: {elements.Count}",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Success"] = model.Publish
            ? "تم نشر التصميم. سيظهر الآن في جميع عمليات التحقق الجديدة."
            : saveAsDraft
                ? "تم حفظ نسخة مسودة آمنة، والتصميم المنشور لم يتغير."
                : "تم حفظ التصميم كمسودة.";

        return RedirectToAction(nameof(Index), new { id = design.CertificateDesignId });
    }

    /// <summary>
    /// Deletes a draft design. The published design and any design still assigned to a course are
    /// protected: removing them would break certificate rendering or the public verification page.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await CanManageAsync()) return Forbid();

        var design = await _db.CertificateDesigns
            .Include(t => t.Elements)
            .FirstOrDefaultAsync(t => t.CertificateDesignId == id);

        if (design == null)
            return NotFound();

        if (design.IsPublished)
        {
            // Keep at least one published template as the fallback for courses without their own.
            var hasAnotherPublished = await _db.CertificateDesigns
                .AnyAsync(t => t.IsPublished && t.CertificateDesignId != id);
            if (!hasAnotherPublished)
            {
                TempData["Error"] = "لا يمكن حذف القالب المنشور الوحيد. انشر قالبًا آخر أولًا ثم أعد المحاولة.";
                return RedirectToAction(nameof(Index), new { id });
            }
        }

        if (await _db.Courses.AnyAsync(course => course.CertificateDesignId == id))
        {
            TempData["Error"] = "لا يمكن حذف تصميم مرتبط بدورة. أزل الربط من صفحة الدورة أولًا.";
            return RedirectToAction(nameof(Index), new { id });
        }

        var name = design.Name;
        await DeleteBackgroundImageIfUnreferencedAsync(design.BackgroundImageUrl, id);
        _db.CertificateDesignElements.RemoveRange(design.Elements);
        _db.CertificateDesigns.Remove(design);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(
            User.Identity?.Name ?? string.Empty,
            "Delete",
            "CertificateDesign",
            id,
            null,
            $"Design: {name}",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Success"] = "تم حذف التصميم.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Duplicates a design, including its elements, so a template can be reused as a starting point.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        if (!await CanManageAsync()) return Forbid();

        var source = await _db.CertificateDesigns
            .Include(t => t.Elements)
            .FirstOrDefaultAsync(t => t.CertificateDesignId == id);

        if (source == null)
            return NotFound();

        var copy = await CloneDesignAsync(source, " (نسخة)", publish: false);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(
            User.Identity?.Name ?? string.Empty,
            "Duplicate",
            "CertificateDesign",
            copy.CertificateDesignId,
            null,
            $"Design: {copy.Name} (from {source.Name})",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Success"] = "تم تكرار التصميم.";
        return RedirectToAction(nameof(Index), new { id = copy.CertificateDesignId });
    }

    /// <summary>
    /// Renames a design without touching its artwork. Kept separate from <see cref="Save"/> so a
    /// rename can never be confused with publishing, and so it works on the published design too.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(int id, string name)
    {
        if (!await CanManageAsync()) return Forbid();

        var design = await _db.CertificateDesigns.FirstOrDefaultAsync(t => t.CertificateDesignId == id);
        if (design == null)
            return NotFound();

        var trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            TempData["Error"] = "لا يمكن أن يكون اسم القالب فارغًا.";
            return RedirectToAction(nameof(Index), new { id });
        }

        var previous = design.Name;
        design.Name = TrimTo(trimmed, 120, previous);
        design.UpdatedBy = TrimTo(User.Identity?.Name, 100, "system");
        design.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(
            User.Identity?.Name ?? string.Empty,
            "Rename",
            "CertificateDesign",
            id,
            null,
            $"Design: {previous} -> {design.Name}",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Success"] = "تمت إعادة تسمية القالب.";
        return RedirectToAction(nameof(Index), new { id });
    }

    /// <summary>
    /// Copies a design and its elements into a new, unpublished row. The caller is responsible for
    /// calling <c>SaveChangesAsync</c>; the clone is added to the change tracker but not saved.
    /// </summary>
    private async Task<CertificateDesign> CloneDesignAsync(CertificateDesign source, string nameSuffix, bool publish = false)
    {
        var copy = new CertificateDesign
        {
            Name = TrimTo(source.Name + nameSuffix, 120, "تصميم شهادة"),
            DesignKey = $"design-{Guid.NewGuid():N}",
            IsPublished = publish,
            CanvasWidth = source.CanvasWidth,
            CanvasHeight = source.CanvasHeight,
            BackgroundColor = source.BackgroundColor,
            BorderColor = source.BorderColor,
            BorderWidth = source.BorderWidth,
            BorderRadius = source.BorderRadius,
            BackgroundImageUrl = source.BackgroundImageUrl,
            UpdatedBy = TrimTo(User.Identity?.Name, 100, "system"),
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        foreach (var element in source.Elements)
        {
            copy.Elements.Add(new CertificateDesignElement
            {
                ElementType = element.ElementType,
                FieldKey = element.FieldKey,
                Content = element.Content,
                X = element.X,
                Y = element.Y,
                Width = element.Width,
                Height = element.Height,
                FontSize = element.FontSize,
                FontFamily = element.FontFamily,
                FontColor = element.FontColor,
                FontWeight = element.FontWeight,
                TextAlign = element.TextAlign,
                IsItalic = element.IsItalic,
                IsUnderline = element.IsUnderline,
                LineHeight = element.LineHeight,
                LetterSpacing = element.LetterSpacing,
                GroupId = element.GroupId,
                IsVisible = element.IsVisible,
                IsLocked = element.IsLocked,
                ZIndex = element.ZIndex,
                Rotation = element.Rotation,
                Opacity = element.Opacity,
                SortOrder = element.SortOrder
            });
        }

        _db.CertificateDesigns.Add(copy);
        // Flush so the clone gets its identity before the caller keeps working with it.
        await _db.SaveChangesAsync();
        return copy;
    }

    private static List<CertificateDesignElement> ParseElements(string? json, int canvasWidth, int canvasHeight, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
            return new List<CertificateDesignElement>();

        List<DesignElementPayload>? payloads;
        try
        {
            payloads = JsonSerializer.Deserialize<List<DesignElementPayload>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            error = "تعذر قراءة عناصر التصميم. أعد تحميل الصفحة وحاول مرة أخرى.";
            return new List<CertificateDesignElement>();
        }

        if (payloads == null || payloads.Count > 100)
        {
            error = "يسمح التصميم بحد أقصى 100 عنصر للحفاظ على سرعة العرض.";
            return new List<CertificateDesignElement>();
        }

        var result = new List<CertificateDesignElement>();
        foreach (var payload in payloads)
        {
            var type = (payload.ElementType ?? string.Empty).Trim().ToLowerInvariant();
            var fieldKey = (payload.FieldKey ?? string.Empty).Trim().ToLowerInvariant();
            if (!CertificateDesignService.IsValidElementType(type))
            {
                error = "نوع عنصر غير مسموح به.";
                return new List<CertificateDesignElement>();
            }

            if (type == "field" && !CertificateDesignService.IsValidFieldKey(fieldKey))
            {
                error = "تم اختيار حقل غير مسموح به.";
                return new List<CertificateDesignElement>();
            }

            // A static image carries an uploaded file path in Content; anything that is not one of
            // our own upload paths is dropped rather than stored.
            var isUploadedArtwork = CertificateDesignService.UploadedArtworkElementTypes.Contains(type);
            var content = TrimTo(payload.Content, 1000, string.Empty);
            if (isUploadedArtwork && !CertificateDesignService.IsUploadedArtworkUrl(content))
            {
                if (content.Length > 0)
                {
                    error = "مسار الصورة غير صالح. أعد رفع الصورة وحاول مرة أخرى.";
                    return new List<CertificateDesignElement>();
                }
                content = string.Empty;
            }

            // Partner elements store their partner slot (0-based) in FieldKey; everything else keeps
            // the key empty because it carries no dynamic reference.
            var isPartnerSlot = CertificateDesignService.PartnerSlotElementTypes.Contains(type);
            var normalizedFieldKey = type == "field"
                ? fieldKey
                : isPartnerSlot
                    ? CertificateDesignService.ParsePartnerSlot(fieldKey).ToString()
                    : string.Empty;

            var element = new CertificateDesignElement
            {
                ElementType = type,
                FieldKey = normalizedFieldKey,
                Content = content,
                X = Math.Clamp(payload.X, 0, canvasWidth - 20),
                Y = Math.Clamp(payload.Y, 0, canvasHeight - 20),
                Width = Math.Clamp(payload.Width, 20, canvasWidth),
                Height = Math.Clamp(payload.Height, 20, canvasHeight),
                FontSize = Math.Clamp(payload.FontSize, 8, 96),
                FontFamily = CertificateDesignService.NormalizeFontFamily(payload.FontFamily),
                FontColor = CertificateDesignService.NormalizeColor(payload.FontColor, "#172033"),
                FontWeight = CertificateDesignService.NormalizeFontWeight(payload.FontWeight),
                TextAlign = CertificateDesignService.NormalizeTextAlign(payload.TextAlign),
                IsItalic = payload.IsItalic,
                IsUnderline = payload.IsUnderline,
                LineHeight = Math.Clamp(payload.LineHeight <= 0 ? 130 : payload.LineHeight, 80, 300),
                LetterSpacing = Math.Clamp(payload.LetterSpacing, -10, 40),
                GroupId = NormalizeGroupId(payload.GroupId),
                IsVisible = payload.IsVisible,
                IsLocked = payload.IsLocked,
                ZIndex = Math.Clamp(payload.ZIndex, -100, 100),
                Rotation = Math.Clamp(payload.Rotation, -180, 180),
                Opacity = Math.Clamp(payload.Opacity <= 0 ? 100 : payload.Opacity, 10, 100),
                SortOrder = result.Count
            };

            if (type == "divider")
                element.Content = string.Empty;

            result.Add(element);
        }

        return result;
    }

    private static string TrimTo(string? value, int maxLength, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed[..Math.Min(trimmed.Length, maxLength)];
    }

    // A group id is an opaque token the designer assigns; keep it short and safe, and treat an
    // empty value as "ungrouped" so a stray empty string does not read as a real group.
    private static string? NormalizeGroupId(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;
        var safe = new string(trimmed.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        return safe.Length == 0 ? null : safe[..Math.Min(safe.Length, 40)];
    }

    private sealed class DesignElementPayload
    {
        public string? ElementType { get; set; }
        public string? FieldKey { get; set; }
        public string? Content { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FontSize { get; set; }
        public string? FontFamily { get; set; }
        public string? FontColor { get; set; }
        public string? FontWeight { get; set; }
        public string? TextAlign { get; set; }
        public bool IsItalic { get; set; }
        public bool IsUnderline { get; set; }
        public int LineHeight { get; set; } = 130;
        public int LetterSpacing { get; set; }
        public string? GroupId { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; }
        public int ZIndex { get; set; }
        public int Rotation { get; set; }
        public int Opacity { get; set; } = 100;
    }
}
