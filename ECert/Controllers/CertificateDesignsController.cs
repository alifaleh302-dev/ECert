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

    public CertificateDesignsController(ECertDbContext db, AuditLogService audit)
    {
        _db = db;
        _audit = audit;
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
        return View("Index", new CertificateDesignEditorViewModel
        {
            Design = CertificateDesignService.CreateDefault(User.Identity?.Name),
            Designs = designs,
            FieldLabels = CertificateDesignService.FieldLabels
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CertificateDesignEditorPostModel model)
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

        var saveAsDraft = !model.Publish && current?.IsPublished == true;
        CertificateDesign design;
        if (saveAsDraft)
        {
            design = new CertificateDesign
            {
                Name = string.IsNullOrWhiteSpace(model.Name) ? "مسودة شهادة" : model.Name.Trim() + " (مسودة)",
                DesignKey = $"draft-{Guid.NewGuid():N}",
                IsPublished = false,
                CreatedAt = DateTime.Now
            };
            _db.CertificateDesigns.Add(design);
        }
        else
        {
            design = current ?? new CertificateDesign
            {
                Name = string.IsNullOrWhiteSpace(model.Name) ? "تصميم شهادة" : model.Name.Trim(),
                DesignKey = $"design-{Guid.NewGuid():N}",
                CreatedAt = DateTime.Now
            };
            if (current == null)
                _db.CertificateDesigns.Add(design);
        }

        design.Name = TrimTo(model.Name, 120, "تصميم شهادة");
        design.CanvasWidth = canvasWidth;
        design.CanvasHeight = canvasHeight;
        design.BackgroundColor = CertificateDesignService.NormalizeColor(model.BackgroundColor, "#fffdf7");
        design.BorderColor = CertificateDesignService.NormalizeColor(model.BorderColor, "#c9a227");
        design.BorderWidth = Math.Clamp(model.BorderWidth, 0, 24);
        design.BorderRadius = Math.Clamp(model.BorderRadius, 0, 80);
        design.UpdatedBy = TrimTo(User.Identity?.Name, 100, "system");
        design.UpdatedAt = DateTime.Now;
        design.IsPublished = model.Publish;

        if (model.Publish)
        {
            var published = await _db.CertificateDesigns
                .Where(t => t.IsPublished && t.CertificateDesignId != design.CertificateDesignId)
                .ToListAsync();
            foreach (var other in published)
                other.IsPublished = false;
        }

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

            var element = new CertificateDesignElement
            {
                ElementType = type,
                FieldKey = type == "field" ? fieldKey : string.Empty,
                Content = TrimTo(payload.Content, 1000, string.Empty),
                X = Math.Clamp(payload.X, 0, canvasWidth - 20),
                Y = Math.Clamp(payload.Y, 0, canvasHeight - 20),
                Width = Math.Clamp(payload.Width, 20, canvasWidth),
                Height = Math.Clamp(payload.Height, 20, canvasHeight),
                FontSize = Math.Clamp(payload.FontSize, 8, 96),
                FontFamily = CertificateDesignService.NormalizeFontFamily(payload.FontFamily),
                FontColor = CertificateDesignService.NormalizeColor(payload.FontColor, "#172033"),
                FontWeight = CertificateDesignService.NormalizeFontWeight(payload.FontWeight),
                TextAlign = CertificateDesignService.NormalizeTextAlign(payload.TextAlign),
                IsVisible = payload.IsVisible,
                ZIndex = Math.Clamp(payload.ZIndex, -100, 100),
                Rotation = Math.Clamp(payload.Rotation, -180, 180),
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
        public bool IsVisible { get; set; } = true;
        public int ZIndex { get; set; }
        public int Rotation { get; set; }
    }
}
