using ECert.Data;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ECertDbContext _db;
    public DashboardController(ECertDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        // The admin area is reserved for accounts holding at least one permission.
        if (!User.HasAnyAdminPermission()) return Forbid();

        var vm = new DashboardViewModel
        {
            PendingRegistrations = await _db.Registrations.InScope(User).CountAsync(r => r.Status == "Pending"),
            UnpaidInvoices = await _db.Invoices.InScope(User).CountAsync(i => i.Status != "Paid" && i.Status != "Cancelled"),
            ActiveCourses = await _db.Courses.InScope(User).CountAsync(c => c.Status == "OpenForRegistration" || c.Status == "InProgress"),
            NewPosts = await _db.Posts.CountAsync(p => p.Status == "Published" && p.PublishedAt >= DateTime.Now.AddDays(-7)),
            TotalRevenue = await _db.Payments.InScope(User).Where(p => !p.IsCancelled).SumAsync(p => (decimal?)p.Amount) ?? 0m,
            TotalTrainees = await _db.Registrations.InScope(User).CountAsync(r => r.Status == "Accepted"),
            RecentRegistrations = await _db.Registrations.InScope(User).Include(r => r.Course).OrderByDescending(r => r.RegistrationDate).Take(5).ToListAsync(),
            RecentInvoices = await _db.Invoices.InScope(User).OrderByDescending(i => i.CreatedAt).Take(5).ToListAsync()
        };
        return View(vm);
    }
}
