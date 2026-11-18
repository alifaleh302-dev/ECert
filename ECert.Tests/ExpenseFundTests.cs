using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the fund/expense rework: the standalone "withdraw from the fund" feature is gone, an
/// expense is deducted from the main fund as soon as it is recorded (and released again on
/// rejection), the fund balance is computed one way everywhere, and a category budget cap is
/// enforced on both submission and approval.
/// </summary>
[Collection(AppCollection.Name)]
public class ExpenseFundTests
{
    private readonly AppFixture _fixture;

    public ExpenseFundTests(AppFixture fixture) => _fixture = fixture;

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        return client;
    }

    // ===== The withdrawal feature is retired =====

    [Fact]
    public async Task The_legacy_withdrawal_table_is_dropped()
    {
        Assert.False(await _fixture.Factory.TableExistsAsync("CashboxWithdrawals"));
    }

    [Fact]
    public async Task The_withdraw_endpoint_no_longer_exists()
    {
        var admin = await SignInAdminAsync();
        // The action is gone, so the router has no match. Posting without a token also proves no
        // antiforgery-protected form remains behind that path.
        var response = await admin.PostWithoutTokenAsync("/Cashbox/Withdraw", new[]
        {
            new KeyValuePair<string, string>("amount", "100"),
            new KeyValuePair<string, string>("reason", "اختبار")
        });
        Assert.True((int)response.StatusCode is 404 or 405,
            $"expected 404/405, got {(int)response.StatusCode}");
    }

    // ===== Expenses move the main fund =====

    [Fact]
    public async Task A_handover_is_the_only_way_into_the_fund()
    {
        var before = await _fixture.Factory.MainFundBalanceAsync();
        await _fixture.Factory.AddFundTransferAsync(500m);
        Assert.Equal(before + 500m, await _fixture.Factory.MainFundBalanceAsync());
    }

    [Fact]
    public async Task An_approved_expense_is_deducted_from_the_main_fund()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند اختبار");
        var before = await _fixture.Factory.MainFundBalanceAsync();

        await _fixture.Factory.CreateExpenseAsync(categoryId, 250m, ExpenseStatus.Approved);

        Assert.Equal(before - 250m, await _fixture.Factory.MainFundBalanceAsync());
    }

    [Fact]
    public async Task A_pending_expense_does_not_move_the_fund_until_approved()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند اختبار");
        var before = await _fixture.Factory.MainFundBalanceAsync();

        var expenseId = await _fixture.Factory.CreateExpenseAsync(categoryId, 400m, ExpenseStatus.PendingApproval);

        // A pending expense is not a movement yet: the fund is untouched.
        Assert.Equal(before, await _fixture.Factory.MainFundBalanceAsync());

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/Expenses", "/Expenses/Approve", new[]
        {
            new KeyValuePair<string, string>("id", expenseId.ToString())
        });
        response.EnsureSuccessStatusCode();

        // Approval is what turns it into a movement that leaves the fund.
        Assert.Equal(ExpenseStatus.Approved, await _fixture.Factory.ExpenseStatusAsync(expenseId));
        Assert.Equal(before - 400m, await _fixture.Factory.MainFundBalanceAsync());
    }

    [Fact]
    public async Task Rejecting_a_pending_expense_leaves_the_fund_unchanged()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند اختبار");
        var before = await _fixture.Factory.MainFundBalanceAsync();

        var expenseId = await _fixture.Factory.CreateExpenseAsync(categoryId, 300m, ExpenseStatus.PendingApproval);

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/Expenses", "/Expenses/Reject", new[]
        {
            new KeyValuePair<string, string>("id", expenseId.ToString())
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal(ExpenseStatus.Rejected, await _fixture.Factory.ExpenseStatusAsync(expenseId));
        Assert.Equal(before, await _fixture.Factory.MainFundBalanceAsync());
    }

    [Fact]
    public async Task Every_reader_of_the_fund_balance_agrees()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند اختبار");
        await _fixture.Factory.CreateExpenseAsync(categoryId, 150m, ExpenseStatus.Approved);

        using var scope = _fixture.Factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<FinancialLedgerService>();
        var reports = scope.ServiceProvider.GetRequiredService<FinanceReportService>();
        var reconciliation = scope.ServiceProvider.GetRequiredService<CashReconciliationService>();

        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "SuperAdmin")
            }, "test"));

        var expected = await ledger.GetMainFundBalanceAsync();
        Assert.Equal(expected, (await reports.GetDashboardAsync(principal)).FundBalance);

        var recon = await reconciliation.ReconcileAsync(DateTime.Today.AddDays(-1), DateTime.Today);
        Assert.Equal(expected, recon.FundBalance);
    }

    // ===== Category budget caps =====

    [Fact]
    public async Task The_category_management_page_renders_with_the_budget_columns()
    {
        await _fixture.Factory.CreateExpenseCategoryAsync("بند عرض", maxAmount: 1000m);

        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/ExpenseCategories");

        Assert.Contains("السقف", html);
        Assert.Contains("المستهلك", html);
        Assert.Contains("المتبقي", html);
        Assert.Contains("الحد الأقصى للميزانية", html);
    }

    [Fact]
    public async Task A_category_in_use_cannot_be_deleted()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند مستخدم");
        await _fixture.Factory.CreateExpenseAsync(categoryId, 10m, ExpenseStatus.PendingApproval);

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/ExpenseCategories", "/ExpenseCategories/Delete", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString())
        });
        response.EnsureSuccessStatusCode();

        var stillThere = await _fixture.Factory.WithDbAsync(db =>
            db.ExpenseCategories.AnyAsync(c => c.ExpenseCategoryId == categoryId));
        Assert.True(stillThere);
    }

    [Fact]
    public async Task An_unused_category_can_be_deleted()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند حر");

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/ExpenseCategories", "/ExpenseCategories/Delete", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString())
        });
        response.EnsureSuccessStatusCode();

        var stillThere = await _fixture.Factory.WithDbAsync(db =>
            db.ExpenseCategories.AnyAsync(c => c.ExpenseCategoryId == categoryId));
        Assert.False(stillThere);
    }

    [Fact]
    public async Task A_category_cap_rejects_a_submission_that_would_overshoot()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند بسقف", maxAmount: 500m);

        var admin = await SignInAdminAsync();
        var before = await _fixture.Factory.MainFundBalanceAsync();
        var response = await admin.PostFormAsync("/Expenses/Create", "/Expenses/Create", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("description", "يتجاوز السقف"),
            new KeyValuePair<string, string>("amount", "600")
        });
        response.EnsureSuccessStatusCode();

        // Nothing was recorded, so the fund is untouched.
        var count = await _fixture.Factory.WithDbAsync(db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(0, count);
        Assert.Equal(before, await _fixture.Factory.MainFundBalanceAsync());
    }

    [Fact]
    public async Task A_category_cap_allows_a_submission_within_the_limit()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند بسقف", maxAmount: 500m);

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/Expenses/Create", "/Expenses/Create", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("description", "داخل السقف"),
            new KeyValuePair<string, string>("amount", "400")
        });
        response.EnsureSuccessStatusCode();

        var count = await _fixture.Factory.WithDbAsync(db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_category_cap_blocks_approval_that_would_overshoot()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند بسقف", maxAmount: 500m);

        // One approved expense of 400 already consumes most of the cap; a second pending expense of
        // 400 was within the cap when submitted but approving it would take the category to 800.
        await _fixture.Factory.CreateExpenseAsync(categoryId, 400m, ExpenseStatus.Approved);
        var second = await _fixture.Factory.CreateExpenseAsync(categoryId, 400m, ExpenseStatus.PendingApproval);

        var admin = await SignInAdminAsync();
        var approve = await admin.PostFormAsync("/Expenses", "/Expenses/Approve", new[]
        {
            new KeyValuePair<string, string>("id", second.ToString())
        });
        approve.EnsureSuccessStatusCode();

        // The approval is refused, so the expense stays pending and the cap holds.
        Assert.Equal(ExpenseStatus.PendingApproval, await _fixture.Factory.ExpenseStatusAsync(second));
    }

    // ===== The submitter may withdraw their own pending request =====

    [Fact]
    public async Task The_submitter_can_cancel_their_own_pending_expense()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند إلغاء");
        var adminUserId = await _fixture.Factory.AdminUserIdAsync();
        var expenseId = await _fixture.Factory.CreateExpenseAsync(categoryId, 120m,
            ExpenseStatus.PendingApproval, submittedByUserId: adminUserId);

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/Expenses", "/Expenses/Cancel", new[]
        {
            new KeyValuePair<string, string>("id", expenseId.ToString())
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal(ExpenseStatus.Cancelled, await _fixture.Factory.ExpenseStatusAsync(expenseId));
    }

    [Fact]
    public async Task A_cancelled_expense_releases_its_category_budget()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند إلغاء", maxAmount: 500m);
        var adminUserId = await _fixture.Factory.AdminUserIdAsync();
        var expenseId = await _fixture.Factory.CreateExpenseAsync(categoryId, 400m,
            ExpenseStatus.PendingApproval, submittedByUserId: adminUserId);

        var admin = await SignInAdminAsync();
        await admin.PostFormAsync("/Expenses", "/Expenses/Cancel", new[]
        {
            new KeyValuePair<string, string>("id", expenseId.ToString())
        });

        // With the pending expense cancelled, a fresh submission of 500 fits under the cap again.
        var again = await admin.PostFormAsync("/Expenses/Create", "/Expenses/Create", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("description", "بعد الإلغاء"),
            new KeyValuePair<string, string>("amount", "500")
        });
        again.EnsureSuccessStatusCode();

        var count = await _fixture.Factory.WithDbAsync(db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId
                && e.Status == ExpenseStatus.PendingApproval));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task An_approver_cannot_cancel_someone_elses_expense_through_the_cancel_action()
    {
        // The cancel action is submitter-only: an expense owned by a different user id is refused
        // even for a SuperAdmin (who approves, not withdraws).
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند مملوك");
        var expenseId = await _fixture.Factory.CreateExpenseAsync(categoryId, 60m,
            ExpenseStatus.PendingApproval, submittedByUserId: 999_999);

        var admin = await SignInAdminAsync();
        await admin.PostFormAsync("/Expenses", "/Expenses/Cancel", new[]
        {
            new KeyValuePair<string, string>("id", expenseId.ToString())
        });

        Assert.Equal(ExpenseStatus.PendingApproval, await _fixture.Factory.ExpenseStatusAsync(expenseId));
    }

    [Fact]
    public async Task An_approved_expense_cannot_be_cancelled()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند معتمد");
        var adminUserId = await _fixture.Factory.AdminUserIdAsync();
        var expenseId = await _fixture.Factory.CreateExpenseAsync(categoryId, 80m,
            ExpenseStatus.Approved, submittedByUserId: adminUserId);

        var admin = await SignInAdminAsync();
        await admin.PostFormAsync("/Expenses", "/Expenses/Cancel", new[]
        {
            new KeyValuePair<string, string>("id", expenseId.ToString())
        });

        Assert.Equal(ExpenseStatus.Approved, await _fixture.Factory.ExpenseStatusAsync(expenseId));
    }

    // ===== Multiple attachments (up to five) =====

    [Fact]
    public async Task A_submission_can_carry_up_to_five_images()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند مرفقات");

        var admin = await SignInAdminAsync();
        var png = PngBytes();
        var files = Enumerable.Range(1, 5)
            .Select(i => ($"attachments", $"img{i}.png", png, "image/png"))
            .ToList();

        var response = await admin.PostMultipartAsync("/Expenses/Create", "/Expenses/Create", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("description", "خمس صور"),
            new KeyValuePair<string, string>("amount", "100")
        }, files);
        response.EnsureSuccessStatusCode();

        var expenseId = await _fixture.Factory.WithDbAsync(db =>
            db.Expenses.Where(e => e.ExpenseCategoryId == categoryId).Select(e => e.ExpenseId).SingleAsync());
        var urls = await _fixture.Factory.ExpenseAttachmentUrlsAsync(expenseId);
        Assert.Equal(5, urls.Count);
        Assert.All(urls, url => Assert.StartsWith("/uploads/expenses/", url));
    }

    [Fact]
    public async Task A_sixth_image_is_rejected_and_nothing_is_recorded()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند تجاوز");
        var png = PngBytes();
        var files = Enumerable.Range(1, 6)
            .Select(i => ($"attachments", $"img{i}.png", png, "image/png"))
            .ToList();

        var admin = await SignInAdminAsync();
        var response = await admin.PostMultipartAsync("/Expenses/Create", "/Expenses/Create", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("description", "ست صور"),
            new KeyValuePair<string, string>("amount", "100")
        }, files);
        response.EnsureSuccessStatusCode();

        var count = await _fixture.Factory.WithDbAsync(db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_non_image_attachment_is_rejected()
    {
        var categoryId = await _fixture.Factory.CreateExpenseCategoryAsync("بند ملف خاطئ");
        // An HTML payload with an image extension must be refused by the magic-byte check.
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

        var admin = await SignInAdminAsync();
        var response = await admin.PostMultipartAsync("/Expenses/Create", "/Expenses/Create", new[]
        {
            new KeyValuePair<string, string>("expenseCategoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("description", "ملف خاطئ"),
            new KeyValuePair<string, string>("amount", "100")
        }, new[] { ($"attachments", "evil.png", html, "image/png") });
        response.EnsureSuccessStatusCode();

        var count = await _fixture.Factory.WithDbAsync(db =>
            db.Expenses.CountAsync(e => e.ExpenseCategoryId == categoryId));
        Assert.Equal(0, count);
    }

    /// <summary>A minimal valid 1x1 PNG, so the upload passes the magic-byte check.</summary>
    private static byte[] PngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M8AAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
}
