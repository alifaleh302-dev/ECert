using ECert.Data;
using ECert.Models;
using ECert.Services;
using ECert.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class HomepageManageController : Controller
{
    private readonly ECertDbContext _db;
    private readonly IWebHostEnvironment _env;

    public HomepageManageController(ECertDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    private bool IsSuperAdmin() => User.IsInRole("SuperAdmin");

    public IActionResult Index()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "إدارة الصفحة الرئيسية";
        return View();
    }

    // ===== SLIDES =====
    public async Task<IActionResult> Slides()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "إدارة الشرائح";
        var slides = await _db.HeroSlides.OrderBy(s => s.SortOrder).ToListAsync();
        return View(slides);
    }

    [HttpPost]
    public async Task<IActionResult> SaveSlide(HeroSlide model, IFormFile? ImageFile)
    {
        if (!IsSuperAdmin()) return Forbid();

        if (ImageFile != null && ImageFile.Length > 0)
        {
            var (valid, validationError) = await ImageUploadValidator.ValidateAsync(ImageFile);
            if (!valid)
            {
                TempData["Error"] = validationError;
                return RedirectToAction("Slides");
            }

            var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "slides");
            Directory.CreateDirectory(uploadsDir);
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(ImageFile.FileName)}";
            using var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.Create);
            await ImageFile.CopyToAsync(stream);

            // Delete old image if exists
            if (!string.IsNullOrEmpty(model.ImageUrl))
            {
                var oldPath = Path.Combine(_env.WebRootPath, model.ImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
            }
            model.ImageUrl = $"/uploads/slides/{fileName}";
        }

        if (model.HeroSlideId == 0)
        {
            model.SortOrder = await _db.HeroSlides.MaxAsync(s => (int?)s.SortOrder) + 1 ?? 1;
            _db.HeroSlides.Add(model);
        }
        else
        {
            var existing = await _db.HeroSlides.FindAsync(model.HeroSlideId);
            if (existing == null) return NotFound();
            existing.Title = model.Title;
            existing.Description = model.Description;
            existing.Button1Text = model.Button1Text;
            existing.Button1Url = model.Button1Url;
            existing.Button2Text = model.Button2Text;
            existing.Button2Url = model.Button2Url;
            existing.IsActive = model.IsActive;
            if (!string.IsNullOrEmpty(model.ImageUrl)) existing.ImageUrl = model.ImageUrl;
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ الشريحة بنجاح";
        return RedirectToAction("Slides");
    }

    [HttpPost]
    public async Task<IActionResult> DeleteSlide(int id)
    {
        if (!IsSuperAdmin()) return Forbid();
        var slide = await _db.HeroSlides.FindAsync(id);
        if (slide != null)
        {
            if (!string.IsNullOrEmpty(slide.ImageUrl))
            {
                var path = Path.Combine(_env.WebRootPath, slide.ImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            _db.HeroSlides.Remove(slide);
            await _db.SaveChangesAsync();
        }
        TempData["Success"] = "تم حذف الشريحة";
        return RedirectToAction("Slides");
    }

    [HttpPost]
    public async Task<IActionResult> MoveSlide(int id, int direction)
    {
        if (!IsSuperAdmin()) return Forbid();
        var slides = await _db.HeroSlides.OrderBy(s => s.SortOrder).ToListAsync();
        var slide = slides.FirstOrDefault(s => s.HeroSlideId == id);
        if (slide == null) return NotFound();

        var idx = slides.IndexOf(slide);
        var swapIdx = idx + direction;
        if (swapIdx < 0 || swapIdx >= slides.Count) return RedirectToAction("Slides");

        var other = slides[swapIdx];
        (slide.SortOrder, other.SortOrder) = (other.SortOrder, slide.SortOrder);
        await _db.SaveChangesAsync();
        return RedirectToAction("Slides");
    }

    // ===== ANIMATED TEXTS =====
    public async Task<IActionResult> AnimatedTexts()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "النصوص المتحركة";
        var texts = await _db.HeroAnimatedTexts.OrderBy(t => t.SortOrder).ToListAsync();
        return View(texts);
    }

    [HttpPost]
    public async Task<IActionResult> AddAnimatedText(string text)
    {
        if (!IsSuperAdmin()) return Forbid();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var order = await _db.HeroAnimatedTexts.MaxAsync(t => (int?)t.SortOrder) + 1 ?? 1;
            _db.HeroAnimatedTexts.Add(new HeroAnimatedText { Text = text.Trim(), SortOrder = order, IsActive = true });
            await _db.SaveChangesAsync();
        }
        TempData["Success"] = "تم إضافة النص";
        return RedirectToAction("AnimatedTexts");
    }

    [HttpPost]
    public async Task<IActionResult> DeleteAnimatedText(int id)
    {
        if (!IsSuperAdmin()) return Forbid();
        var text = await _db.HeroAnimatedTexts.FindAsync(id);
        if (text != null) { _db.HeroAnimatedTexts.Remove(text); await _db.SaveChangesAsync(); }
        TempData["Success"] = "تم حذف النص";
        return RedirectToAction("AnimatedTexts");
    }

    [HttpPost]
    public async Task<IActionResult> ToggleAnimatedText(int id)
    {
        if (!IsSuperAdmin()) return Forbid();
        var text = await _db.HeroAnimatedTexts.FindAsync(id);
        if (text != null) { text.IsActive = !text.IsActive; await _db.SaveChangesAsync(); }
        return RedirectToAction("AnimatedTexts");
    }

    // ===== SECTIONS =====
    public async Task<IActionResult> Sections()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "إدارة الأقسام";
        var sections = await _db.HomepageSections.OrderBy(s => s.SortOrder).ToListAsync();
        return View(sections);
    }

    [HttpPost]
    public async Task<IActionResult> ToggleSection(int id)
    {
        if (!IsSuperAdmin()) return Forbid();
        var section = await _db.HomepageSections.FindAsync(id);
        if (section != null) { section.IsVisible = !section.IsVisible; await _db.SaveChangesAsync(); }
        TempData["Success"] = "تم تحديث القسم";
        return RedirectToAction("Sections");
    }

    [HttpPost]
    public async Task<IActionResult> MoveSection(int id, int direction)
    {
        if (!IsSuperAdmin()) return Forbid();
        var sections = await _db.HomepageSections.OrderBy(s => s.SortOrder).ToListAsync();
        var section = sections.FirstOrDefault(s => s.HomepageSectionId == id);
        if (section == null) return NotFound();

        var idx = sections.IndexOf(section);
        var swapIdx = idx + direction;
        if (swapIdx < 0 || swapIdx >= sections.Count) return RedirectToAction("Sections");

        var other = sections[swapIdx];
        (section.SortOrder, other.SortOrder) = (other.SortOrder, section.SortOrder);
        await _db.SaveChangesAsync();
        return RedirectToAction("Sections");
    }

    // ===== STATS =====
    public async Task<IActionResult> Stats()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "إدارة الإحصائيات";
        var stats = await _db.StatCards.OrderBy(s => s.SortOrder).ToListAsync();
        return View(stats);
    }

    [HttpPost]
    public async Task<IActionResult> SaveStats(List<StatCard> stats)
    {
        if (!IsSuperAdmin()) return Forbid();
        foreach (var s in stats)
        {
            var existing = await _db.StatCards.FindAsync(s.StatCardId);
            if (existing == null) continue;
            existing.Label = s.Label;
            existing.IconClass = s.IconClass;
            existing.Color = s.Color;
            existing.IsActive = s.IsActive;
            existing.IsDynamic = s.IsDynamic;
            existing.DynamicSource = s.IsDynamic ? s.DynamicSource : null;
            existing.StaticValue = s.IsDynamic ? 0 : s.StaticValue;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ الإحصائيات";
        return RedirectToAction("Stats");
    }

    // ===== ABOUT & FOOTER =====
    public async Task<IActionResult> AboutFooter()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "من نحن والفوتر";

        var model = new HomepageContentSettingsViewModel
        {
            AboutTitle = await GetSiteSettingAsync("AboutTitle", "من نحن"),
            AboutSummary = await GetSiteSettingAsync("AboutSummary", await GetSiteSettingAsync("MetaDescription", string.Empty)),
            AboutContent = await GetSiteSettingAsync("AboutContent", string.Empty),
            FooterEnabled = await GetSiteSettingBoolAsync("FooterEnabled", true),
            FooterShowQuickLinks = await GetSiteSettingBoolAsync("FooterShowQuickLinks", true),
            FooterShowContact = await GetSiteSettingBoolAsync("FooterShowContact", true),
            FooterShowAboutLinks = await GetSiteSettingBoolAsync("FooterShowAboutLinks", true),
            FooterShowSocial = await GetSiteSettingBoolAsync("FooterShowSocial", true)
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAboutFooter(HomepageContentSettingsViewModel model)
    {
        if (!IsSuperAdmin()) return Forbid();

        await UpsertSiteSettingAsync("AboutTitle", model.AboutTitle?.Trim(), "Content");
        await UpsertSiteSettingAsync("AboutSummary", model.AboutSummary?.Trim(), "Content");
        await UpsertSiteSettingAsync("AboutContent", model.AboutContent?.Trim(), "Content");
        await UpsertSiteSettingAsync("FooterEnabled", model.FooterEnabled.ToString().ToLowerInvariant(), "Layout");
        await UpsertSiteSettingAsync("FooterShowQuickLinks", model.FooterShowQuickLinks.ToString().ToLowerInvariant(), "Layout");
        await UpsertSiteSettingAsync("FooterShowContact", model.FooterShowContact.ToString().ToLowerInvariant(), "Layout");
        await UpsertSiteSettingAsync("FooterShowAboutLinks", model.FooterShowAboutLinks.ToString().ToLowerInvariant(), "Layout");
        await UpsertSiteSettingAsync("FooterShowSocial", model.FooterShowSocial.ToString().ToLowerInvariant(), "Layout");
        await _db.SaveChangesAsync();

        TempData["Success"] = "تم حفظ إعدادات من نحن والفوتر بنجاح";
        return RedirectToAction(nameof(AboutFooter));
    }

    // ===== CONTACT =====
    public async Task<IActionResult> Contact()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "معلومات التواصل";
        var contact = await _db.ContactInfos.FirstOrDefaultAsync();
        if (contact == null)
        {
            contact = new ContactInfo();
            _db.ContactInfos.Add(contact);
            await _db.SaveChangesAsync();
        }
        return View(contact);
    }

    [HttpPost]
    public async Task<IActionResult> SaveContact(ContactInfo model)
    {
        if (!IsSuperAdmin()) return Forbid();
        var existing = await _db.ContactInfos.FirstOrDefaultAsync();
        if (existing == null) { _db.ContactInfos.Add(model); }
        else
        {
            existing.Phone = model.Phone;
            existing.Mobile = model.Mobile;
            existing.Email = model.Email;
            existing.Website = model.Website;
            existing.Address = model.Address;
            existing.WorkingHours = model.WorkingHours;
            existing.ShowPhone = model.ShowPhone;
            existing.ShowMobile = model.ShowMobile;
            existing.ShowEmail = model.ShowEmail;
            existing.ShowWebsite = model.ShowWebsite;
            existing.ShowAddress = model.ShowAddress;
            existing.ShowWorkingHours = model.ShowWorkingHours;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ معلومات التواصل";
        return RedirectToAction("Contact");
    }

    // ===== SOCIAL =====
    public async Task<IActionResult> Social()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "روابط التواصل الاجتماعي";
        var links = await _db.SocialLinks.OrderBy(s => s.SortOrder).ToListAsync();
        return View(links);
    }

    [HttpPost]
    public async Task<IActionResult> SaveSocial(List<SocialLink> links)
    {
        if (!IsSuperAdmin()) return Forbid();
        foreach (var l in links)
        {
            var existing = await _db.SocialLinks.FindAsync(l.SocialLinkId);
            if (existing == null) continue;
            existing.PlatformName = l.PlatformName;
            existing.IconClass = l.IconClass;
            existing.Url = l.Url;
            existing.IsActive = l.IsActive;
            existing.SortOrder = l.SortOrder;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ الروابط";
        return RedirectToAction("Social");
    }

    [HttpPost]
    public async Task<IActionResult> AddSocial(string platformName, string iconClass, string url)
    {
        if (!IsSuperAdmin()) return Forbid();
        if (!string.IsNullOrWhiteSpace(platformName))
        {
            var order = await _db.SocialLinks.MaxAsync(s => (int?)s.SortOrder) + 1 ?? 1;
            _db.SocialLinks.Add(new SocialLink { PlatformName = platformName.Trim(), IconClass = iconClass.Trim(), Url = url?.Trim(), SortOrder = order, IsActive = true });
            await _db.SaveChangesAsync();
        }
        TempData["Success"] = "تم إضافة المنصة";
        return RedirectToAction("Social");
    }

    [HttpPost]
    public async Task<IActionResult> DeleteSocial(int id)
    {
        if (!IsSuperAdmin()) return Forbid();
        var link = await _db.SocialLinks.FindAsync(id);
        if (link != null) { _db.SocialLinks.Remove(link); await _db.SaveChangesAsync(); }
        TempData["Success"] = "تم حذف المنصة";
        return RedirectToAction("Social");
    }

    // ===== THEME =====
    public async Task<IActionResult> Theme()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "الألوان والهوية البصرية";
        var theme = await _db.ThemeSettings.FirstOrDefaultAsync();
        if (theme == null)
        {
            theme = new ThemeSetting();
            _db.ThemeSettings.Add(theme);
            await _db.SaveChangesAsync();
        }
        return View(theme);
    }

    [HttpPost]
    public async Task<IActionResult> SaveTheme(ThemeSetting model)
    {
        if (!IsSuperAdmin()) return Forbid();
        var existing = await _db.ThemeSettings.FirstOrDefaultAsync();
        if (existing == null) { _db.ThemeSettings.Add(model); }
        else
        {
            existing.PrimaryColor = model.PrimaryColor;
            existing.SecondaryColor = model.SecondaryColor;
            existing.ButtonColor = model.ButtonColor;
            existing.NavbarColor = model.NavbarColor;
            existing.FooterColor = model.FooterColor;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ الألوان";
        return RedirectToAction("Theme");
    }

    // ===== SITE SETTINGS =====
    public async Task<IActionResult> SiteSettings()
    {
        if (!IsSuperAdmin()) return Forbid();
        ViewData["Title"] = "إعدادات الموقع";
        var settings = await _db.SiteSettings.ToListAsync();
        ViewBag.Settings = settings;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> SaveSiteSettings(string siteName, string metaDescription, string seoKeywords, string? copyrightText, IFormFile? LogoFile, IFormFile? FaviconFile)
    {
        if (!IsSuperAdmin()) return Forbid();

        await UpsertSetting("SiteName", siteName);
        await UpsertSetting("MetaDescription", metaDescription);
        await UpsertSetting("SeoKeywords", seoKeywords);
        await UpsertSetting("CopyrightText", copyrightText);

        if (LogoFile != null && LogoFile.Length > 0)
        {
            var url = await UploadSiteFile(LogoFile, "logo");
            await UpsertSetting("LogoUrl", url);
        }
        if (FaviconFile != null && FaviconFile.Length > 0)
        {
            var url = await UploadSiteFile(FaviconFile, "favicon");
            await UpsertSetting("FaviconUrl", url);
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ إعدادات الموقع";
        return RedirectToAction("SiteSettings");
    }

    private async Task UpsertSetting(string key, string? value)
    {
        var setting = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
            _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else
            setting.Value = value;
    }

    private async Task<string> GetSiteSettingAsync(string key, string defaultValue = "")
    {
        var value = await _db.SiteSettings
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private async Task<bool> GetSiteSettingBoolAsync(string key, bool defaultValue)
    {
        var value = await _db.SiteSettings
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private async Task UpsertSiteSettingAsync(string key, string? value, string category)
    {
        var setting = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value, Category = category });
            return;
        }

        setting.Value = value;
        setting.Category = category;
    }

    private async Task<string> UploadSiteFile(IFormFile file, string prefix)
    {
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "site");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{prefix}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        using var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.Create);
        await file.CopyToAsync(stream);
        return $"/uploads/site/{fileName}";
    }
}
