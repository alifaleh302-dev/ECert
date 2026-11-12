using ECert.Data;
using ECert.Models;
using ECert.Services;
using ECert.Services.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Read-only view of the append-only financial audit trail: every collection, refund, cancellation,
/// exemption, handover, transfer and withdrawal, with the acting user, amount and reference. No
/// action here mutates data — the log is only ever appended to elsewhere.
/// </summary>
[Authorize]
public class FinancialAuditController : Controller
{
    private readonly ECertDbContext _db;
    public FinancialAuditController(ECertDbContext db) => _db = db;

    public async Task<IActionResult> Index(string? actionType, string? userName, DateTime? from, DateTime? to, string? search)
    {
        if (!User.HasPermission(AdminPermissions.ViewFinancialAudit)) return Forbid();

        var query = BuildQuery(actionType, userName, from, to, search);
        ViewBag.Entries = await query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.FinancialAuditEntryId)
            .Take(1000).ToListAsync();

        ViewBag.ActionType = actionType;
        ViewBag.UserName = userName;
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        ViewBag.Search = search;
        ViewBag.ActionTypes = await _db.FinancialAuditEntries.Select(e => e.ActionType).Distinct().OrderBy(a => a).ToListAsync();
        ViewBag.Users = await _db.FinancialAuditEntries.Select(e => e.UserName).Distinct().OrderBy(u => u).ToListAsync();
        ViewBag.TotalAmount = await query.SumAsync(e => (decimal?)e.Amount) ?? 0m;
        ViewBag.Count = await query.CountAsync();
        return View();
    }

    public async Task<IActionResult> ExportXlsx(string? actionType, string? userName, DateTime? from, DateTime? to, string? search)
    {
        if (!User.HasPermission(AdminPermissions.ViewFinancialAudit)) return Forbid();

        var entries = await BuildQuery(actionType, userName, from, to, search)
            .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.FinancialAuditEntryId)
            .Take(5000).ToListAsync();

        using var export = new ExcelExport("سجل التدقيق المالي",
            "التاريخ", "المستخدم", "نوع العملية", "المبلغ", "نوع المرجع", "رقم المرجع", "رقم المستند", "التفاصيل");

        foreach (var e in entries)
        {
            export.WriteRow(
                e.Timestamp.ToString("yyyy-MM-dd HH:mm"), e.UserName, ActionLabel(e.ActionType),
                e.Amount, e.ReferenceType, e.ReferenceId, e.ReferenceNumber, e.Details);
        }

        return export.Build("financial_audit");
    }

    private IQueryable<FinancialAuditEntry> BuildQuery(string? actionType, string? userName, DateTime? from, DateTime? to, string? search)
    {
        var query = _db.FinancialAuditEntries.AsQueryable();
        if (!string.IsNullOrWhiteSpace(actionType)) query = query.Where(e => e.ActionType == actionType);
        if (!string.IsNullOrWhiteSpace(userName)) query = query.Where(e => e.UserName == userName);
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value.Date);
        if (to.HasValue) query = query.Where(e => e.Timestamp < to.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e => (e.Details != null && e.Details.Contains(search))
                || (e.ReferenceNumber != null && e.ReferenceNumber.Contains(search)));
        return query;
    }

    public static string ActionLabel(string actionType) => actionType switch
    {
        FinancialActionTypes.Collection => "قبض",
        FinancialActionTypes.Refund => "استرداد",
        FinancialActionTypes.Cancel => "إلغاء",
        FinancialActionTypes.Exemption => "إعفاء",
        FinancialActionTypes.Handover => "توريد عهدة",
        FinancialActionTypes.Transfer => "ترحيل رصيد",
        FinancialActionTypes.Withdraw => "سحب",
        _ => actionType
    };
}
