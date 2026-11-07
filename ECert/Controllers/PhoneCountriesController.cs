using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class PhoneCountriesController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    public PhoneCountriesController(ECertDbContext db, AuditLogService audit)
    { _db = db; _audit = audit; }

    private bool CanManage() => User.HasPermission(AdminPermissions.ManagePhoneCountries);

    public async Task<IActionResult> Index()
    {
        if (!CanManage()) return Forbid();
        var list = await _db.PhoneCountries.OrderBy(c => c.CountryName).ToListAsync();
        return View(list);
    }

    [HttpGet]
    public IActionResult Create()
    {
        if (!CanManage()) return Forbid();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PhoneCountry model)
    {
        if (!CanManage()) return Forbid();
        if (!ModelState.IsValid) return View(model);
        _db.PhoneCountries.Add(model);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "PhoneCountry", model.PhoneCountryId, null, model.CountryName);
        TempData["Success"] = "تمت إضافة الدولة بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!CanManage()) return Forbid();
        var item = await _db.PhoneCountries.FindAsync(id);
        if (item == null) return NotFound();
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(PhoneCountry model)
    {
        if (!CanManage()) return Forbid();
        if (!ModelState.IsValid) return View(model);
        var existing = await _db.PhoneCountries.FindAsync(model.PhoneCountryId);
        if (existing == null) return NotFound();
        existing.CountryName = model.CountryName;
        existing.CountryCode = model.CountryCode;
        existing.MinPhoneLength = model.MinPhoneLength;
        existing.MaxPhoneLength = model.MaxPhoneLength;
        existing.Prefixes = model.Prefixes;
        existing.IsActive = model.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "PhoneCountry", model.PhoneCountryId, null, model.CountryName);
        TempData["Success"] = "تم تعديل البيانات بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!CanManage()) return Forbid();
        var item = await _db.PhoneCountries.FindAsync(id);
        if (item == null) return NotFound();
        _db.PhoneCountries.Remove(item);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Delete", "PhoneCountry", id);
        TempData["Success"] = "تم حذف الدولة.";
        return RedirectToAction("Index");
    }
}
