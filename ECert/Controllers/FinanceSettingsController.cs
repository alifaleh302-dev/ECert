using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Financial system settings (إعدادات النظام المالية): the institution's currency name and display
/// symbol, the direct-refund approval ceiling, and the accounting-period lock. Restricted to the
/// built-in SuperAdmin through the <c>manage-finance-settings</c> permission, which is in
/// <see cref="AdminPermissions.SuperAdminOnly"/>.
/// </summary>
[Authorize]
public class FinanceSettingsController : Controller
{
    private readonly ECertDbContext _db;
    private readonly SystemSettingsService _settings;
    private readonly PeriodLockService _periods;
    private readonly FinancialLedgerService _ledger;
    private readonly AuditLogService _audit;
    private readonly SecurityAlertService _alerts;

    public FinanceSettingsController(ECertDbContext db, SystemSettingsService settings, PeriodLockService periods,
        FinancialLedgerService ledger, AuditLogService audit, SecurityAlertService alerts)
    {
        _db = db;
        _settings = settings;
        _periods = periods;
        _ledger = ledger;
        _audit = audit;
        _alerts = alerts;
    }

    private string Actor => User.Identity?.Name ?? "System";

    public async Task<IActionResult> Index()
    {
        if (!User.HasPermission(AdminPermissions.ManageFinanceSettings)) return Forbid();

        ViewBag.CurrencyName = await _settings.GetCurrencyNameAsync();
        ViewBag.CurrencySymbol = await _settings.GetCurrencySymbolAsync();
        ViewBag.RefundThreshold = await _settings.GetRefundApprovalThresholdAsync();
        ViewBag.LockedThrough = await _periods.GetLockedThroughAsync();
        ViewBag.History = await _periods.HistoryAsync();
        ViewBag.CurrencyUsage = await CountCurrencyUsageAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCurrency(string currencyName, string currencySymbol)
    {
        if (!User.HasPermission(AdminPermissions.ManageFinanceSettings)) return Forbid();

        if (string.IsNullOrWhiteSpace(currencyName) || string.IsNullOrWhiteSpace(currencySymbol))
        {
            TempData["Error"] = "اسم العملة ورمزها مطلوبان.";
            return RedirectToAction(nameof(Index));
        }

        await UpsertAsync(SystemSettingsService.CurrencyNameKey, currencyName.Trim());
        await UpsertAsync(SystemSettingsService.CurrencySymbolKey, currencySymbol.Trim());
        await _audit.LogAsync(Actor, "Update", "FinanceSettings", null, null, $"Currency: {currencyName} ({currencySymbol})");
        TempData["Success"] = "تم حفظ إعدادات العملة.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRefundThreshold(decimal? refundApprovalThreshold)
    {
        if (!User.HasPermission(AdminPermissions.ManageFinanceSettings)) return Forbid();

        if (refundApprovalThreshold.HasValue && refundApprovalThreshold.Value < 0)
        {
            TempData["Error"] = "حد الاعتماد لا يمكن أن يكون سالباً.";
            return RedirectToAction(nameof(Index));
        }

        await UpsertAsync(SystemSettingsService.RefundApprovalThresholdKey,
            (refundApprovalThreshold ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _audit.LogAsync(Actor, "Update", "FinanceSettings", null, null, $"RefundThreshold: {refundApprovalThreshold}");
        TempData["Success"] = "تم حفظ حد اعتماد الاسترداد.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockPeriod(DateTime lockedThrough, string? note)
    {
        if (!User.HasPermission(AdminPermissions.ManageFinanceSettings)) return Forbid();

        await _periods.LockAsync(lockedThrough, note, Actor);
        _ledger.AddAudit(Actor, FinancialActionTypes.PeriodLock, 0m,
            details: $"إقفال الفترة المالية حتى {lockedThrough:yyyy-MM-dd}{(string.IsNullOrWhiteSpace(note) ? "" : $" — {note}")}");
        await _db.SaveChangesAsync();
        await _audit.LogAsync(Actor, "LockPeriod", "AccountingPeriod", null, null, $"Through: {lockedThrough:yyyy-MM-dd}");
        TempData["Success"] = $"تم إقفال الفترة المالية حتى {lockedThrough:yyyy-MM-dd}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReopenPeriod(string? note)
    {
        if (!User.HasPermission(AdminPermissions.ManageFinanceSettings)) return Forbid();

        await _periods.ReopenAsync(Actor, note);
        _ledger.AddAudit(Actor, FinancialActionTypes.PeriodReopen, 0m,
            details: $"إعادة فتح الفترة المالية{(string.IsNullOrWhiteSpace(note) ? "" : $" — {note}")}");
        await _db.SaveChangesAsync();
        await _audit.LogAsync(Actor, "ReopenPeriod", "AccountingPeriod", null, null, note);
        await _alerts.AlertPeriodReopenedAsync(Actor, note);
        TempData["Success"] = "تم إعادة فتح الفترة المالية.";
        return RedirectToAction(nameof(Index));
    }

    private async Task UpsertAsync(string key, string value)
    {
        var setting = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
            _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else
            setting.Value = value;
        await _db.SaveChangesAsync();
    }

    private async Task<int> CountCurrencyUsageAsync()
    {
        // The symbol is display-only, so this only reports how many records would render with it.
        var payments = await _db.Payments.CountAsync();
        var invoices = await _db.Invoices.CountAsync();
        var refunds = await _db.Refunds.CountAsync();
        return payments + invoices + refunds;
    }
}
