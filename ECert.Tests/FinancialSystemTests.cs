using System.Net;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// End-to-end tests for the financial v2 system: collections land in the collector's own custody
/// (separate from the main fund), a refund reduces the invoice balance and debits that custody, and
/// a custody handover only moves money to the main fund once a SuperAdmin approves it. Driven
/// through the real HTTP surface, exactly as an employee and an approver would use it.
/// </summary>
[Collection(AppCollection.Name)]
public class FinancialSystemTests
{
    private readonly AppFixture _fixture;

    public FinancialSystemTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_collection_lands_in_the_collectors_custody()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();
        var custodyBefore = await _fixture.Factory.CustodyBalanceAsync(adminId);
        var collectionsBefore = await _fixture.Factory.CustodyEntryCountAsync(adminId, CustodyEntryTypes.Collection);

        var response = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "300"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal(300m, await _fixture.Factory.CustodyBalanceAsync(adminId) - custodyBefore);
        Assert.Equal(collectionsBefore + 1, await _fixture.Factory.CustodyEntryCountAsync(adminId, CustodyEntryTypes.Collection));
        Assert.True(await _fixture.Factory.FinancialAuditCountAsync(FinancialActionTypes.Collection) >= 1);
    }

    [Fact]
    public async Task The_receipt_page_renders_a_verification_qr_and_whatsapp_link()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();

        // Capture the receipt id from the redirect target of a fresh payment.
        var response = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "150"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        response.EnsureSuccessStatusCode();

        var receiptPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        Assert.StartsWith("/Payments/Receipt", receiptPath);

        var html = await admin.GetStringAsync(receiptPath);
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("سند قبض", decoded);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("wa.me", html);
        Assert.Contains("/r/", html);
    }

    [Fact]
    public async Task A_refund_reduces_the_invoice_balance_and_debits_the_custody()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();
        var custodyBefore = await _fixture.Factory.CustodyBalanceAsync(adminId);

        // Collect 500 (fully pays the invoice), then refund 200.
        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "500"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        var (paidAfterCollection, statusAfterCollection) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(500m, paidAfterCollection);
        Assert.Equal("Paid", statusAfterCollection);

        var refundResponse = await admin.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "200"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        refundResponse.EnsureSuccessStatusCode();

        var (paidAfterRefund, statusAfterRefund) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(300m, paidAfterRefund);
        Assert.Equal("PartiallyPaid", statusAfterRefund);
        Assert.Equal(200m, await _fixture.Factory.RefundedTotalAsync(invoiceId));

        // Custody delta: +500 collection, -200 refund = +300.
        Assert.Equal(300m, await _fixture.Factory.CustodyBalanceAsync(adminId) - custodyBefore);
    }

    [Fact]
    public async Task A_refund_cannot_exceed_the_paid_amount()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();

        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "100"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        var refundResponse = await admin.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "999"),
            new KeyValuePair<string, string>("reason", "محاولة استرداد زائد"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        refundResponse.EnsureSuccessStatusCode();

        Assert.Equal(0m, await _fixture.Factory.RefundedTotalAsync(invoiceId));
        Assert.Equal(100m, (await _fixture.Factory.GetInvoiceAsync(invoiceId)).PaidAmount);
    }

    [Fact]
    public async Task Cancelling_a_payment_reverses_the_custody()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();
        var custodyBefore = await _fixture.Factory.CustodyBalanceAsync(adminId);

        var add = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "400"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        add.EnsureSuccessStatusCode();
        Assert.Equal(400m, await _fixture.Factory.CustodyBalanceAsync(adminId) - custodyBefore);

        var paymentId = await _fixture.Factory.WithDbAsync(async db =>
            (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                db.Payments.Where(p => p.InvoiceId == invoiceId))).PaymentId);

        // The invoice details page carries the antiforgery token for the delete form.
        var token = await admin.FetchTokenAsync($"/Invoices/Details/{invoiceId}");
        var delete = await admin.PostFormWithTokenAsync(token, "/Payments/Delete", new[]
        {
            new KeyValuePair<string, string>("id", paymentId.ToString())
        });
        delete.EnsureSuccessStatusCode();

        // The collection's custody credit is reversed, and the invoice returns to unpaid.
        Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(adminId) - custodyBefore);
        Assert.Equal(0m, (await _fixture.Factory.GetInvoiceAsync(invoiceId)).PaidAmount);
    }

    /// <summary>
    /// Re-posting the cancel form (double-click, back button, retry) must not reverse the collector's
    /// custody a second time — the reversal is idempotent and the custody returns to its starting
    /// balance exactly once.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_payment_twice_reverses_the_custody_only_once()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();
        var custodyBefore = await _fixture.Factory.CustodyBalanceAsync(adminId);

        var add = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "300"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        add.EnsureSuccessStatusCode();
        Assert.Equal(300m, await _fixture.Factory.CustodyBalanceAsync(adminId) - custodyBefore);

        var paymentId = await _fixture.Factory.WithDbAsync(async db =>
            (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                db.Payments.Where(p => p.InvoiceId == invoiceId))).PaymentId);

        var token = await admin.FetchTokenAsync($"/Invoices/Details/{invoiceId}");
        var form = new[] { new KeyValuePair<string, string>("id", paymentId.ToString()) };

        var first = await admin.PostFormWithTokenAsync(token, "/Payments/Cancel", form);
        first.EnsureSuccessStatusCode();
        var second = await admin.PostFormWithTokenAsync(token, "/Payments/Cancel", form);
        second.EnsureSuccessStatusCode();

        Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(adminId) - custodyBefore);
        var reversalEntries = await _fixture.Factory.WithDbAsync(db =>
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                db.CustodyEntries.Where(e => e.ReferenceType == "Payment" && e.ReferenceId == paymentId
                    && e.EntryType == ECert.Models.CustodyEntryTypes.Adjustment)));
        Assert.Equal(1, reversalEntries);
    }

    /// <summary>
    /// A refund still awaiting approval must not affect the invoice: cancelling the payment that it
    /// was created against cannot drive the balance negative.
    /// </summary>
    [Fact]
    public async Task A_pending_refund_does_not_drive_the_invoice_balance_negative()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        await _fixture.Factory.AddPaymentAsync(invoiceId, 200m);

        await _fixture.Factory.WithDbAsync(async db =>
        {
            var invoice = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                db.Invoices.Where(i => i.InvoiceId == invoiceId));
            db.Refunds.Add(new ECert.Models.Refund
            {
                InvoiceId = invoiceId,
                RegistrationId = invoice.RegistrationId,
                Amount = 150m,
                Reason = "اختبار استرداد معلّق",
                RefundMethod = "Cash",
                RefundDate = DateTime.Now,
                RefundedBy = "admin",
                Status = ECert.Models.RefundStatus.PendingApproval,
                CreatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();
            return true;
        });

        var paymentId = await _fixture.Factory.WithDbAsync(async db =>
            (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                db.Payments.Where(p => p.InvoiceId == invoiceId))).PaymentId);

        var token = await admin.FetchTokenAsync($"/Invoices/Details/{invoiceId}");
        var cancel = await admin.PostFormWithTokenAsync(token, "/Payments/Cancel", new[]
        {
            new KeyValuePair<string, string>("id", paymentId.ToString())
        });
        cancel.EnsureSuccessStatusCode();

        var (paid, _) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(0m, paid);
    }

    [Fact]
    public async Task An_employee_requests_a_handover_and_a_super_admin_approves_it()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();
        var custodyBefore = await _fixture.Factory.CustodyBalanceAsync(adminId);
        var handoversBefore = await _fixture.Factory.CustodyEntryCountAsync(adminId, CustodyEntryTypes.Handover);

        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "500"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        // Request a handover of 500.
        var request = await admin.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
        {
            new KeyValuePair<string, string>("amount", "500"),
            new KeyValuePair<string, string>("note", "توريد تحصيل اليوم")
        });
        request.EnsureSuccessStatusCode();
        Assert.Equal(CustodyHandoverStatus.Pending, await _fixture.Factory.LatestHandoverStatusAsync(adminId));

        // Approve it as the SuperAdmin.
        var handoverId = await _fixture.Factory.WithDbAsync(async db =>
            (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                db.CustodyHandovers.Where(h => h.UserId == adminId && h.Status == CustodyHandoverStatus.Pending))).CustodyHandoverId);

        var approve = await admin.PostFormAsync("/Custody", "/Custody/Approve", new[]
        {
            new KeyValuePair<string, string>("id", handoverId.ToString()),
            new KeyValuePair<string, string>("note", "تم الاستلام")
        });
        approve.EnsureSuccessStatusCode();

        Assert.Equal(CustodyHandoverStatus.Approved, await _fixture.Factory.LatestHandoverStatusAsync(adminId));
        // The handover cancels out this test's collection, so the net delta is zero.
        Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(adminId) - custodyBefore);
        Assert.Equal(handoversBefore + 1, await _fixture.Factory.CustodyEntryCountAsync(adminId, CustodyEntryTypes.Handover));
    }

    [Fact]
    public async Task A_handover_beyond_the_custody_balance_is_rejected()
    {
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();

        var response = await admin.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
        {
            new KeyValuePair<string, string>("amount", "100000"),
            new KeyValuePair<string, string>("note", "مبلغ غير متاح")
        });
        response.EnsureSuccessStatusCode();

        Assert.Null(await _fixture.Factory.LatestHandoverStatusAsync(adminId));
    }

    [Fact]
    public async Task The_receivables_page_lists_an_unpaid_invoice_in_scope()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();

        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/Receivables"));
        Assert.Contains("المتأخرات", html);

        // The statement page for the invoice renders the account ledger.
        var statement = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync($"/Receivables/Statement?invoiceId={invoiceId}"));
        Assert.Contains("كشف حساب", statement);
    }

    [Fact]
    public async Task The_financial_audit_log_records_movements()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();

        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "250"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/FinancialAudit"));
        Assert.Contains("سجل التدقيق المالي", html);
        Assert.Contains("قبض", html);
    }

    [Fact]
    public async Task The_public_receipt_verification_endpoint_confirms_a_receipt()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();

        var add = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "120"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        add.EnsureSuccessStatusCode();

        var receiptPath = add.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        var html = await admin.GetStringAsync(receiptPath);

        // Extract the public verification URL from the receipt and fetch it anonymously.
        var match = System.Text.RegularExpressions.Regex.Match(html, "/r/[A-Z0-9]+\\?sig=[A-Za-z0-9_\\-]+");
        Assert.True(match.Success, "receipt should embed a public verification URL");

        var anon = AppClient.Create(_fixture.Factory);
        var verifyHtml = System.Net.WebUtility.HtmlDecode(await anon.GetStringAsync(match.Value));
        Assert.Contains("سند قبض", verifyHtml);
        Assert.Contains("صحيح", verifyHtml);
    }

    [Fact]
    public async Task The_public_receipt_verification_endpoint_rejects_a_bad_signature()
    {
        var anon = AppClient.Create(_fixture.Factory);
        var response = await anon.GetAsync("/r/ABCDEFGH12345678?sig=wrong-signature");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("غير صالح", html);
    }

    [Fact]
    public async Task Custody_permission_gates_the_custody_section()
    {
        // A user granted only the custody permission can open /Custody but not /Refunds.
        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ViewCustody });
        var client = await SignInAsync(user.Username, user.Password);

        Assert.True(await client.CanOpenAsync("/Custody"));
        Assert.False(await client.CanOpenAsync("/Refunds"));
        Assert.False(await client.CanOpenAsync("/FinancialAudit"));
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<AppClient> SignInAsync(string username, string password)
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync(username, password);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<TestUser> CreateUserWithRoleAsync(IEnumerable<string> permissionNames)
    {
        var admin = await SignInAdminAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roleName = $"دور {suffix}";
        var username = $"user{suffix}";

        var permissionIds = new List<int>();
        foreach (var name in permissionNames)
            permissionIds.Add(await _fixture.Factory.PermissionIdAsync(name));

        var roleFields = new List<KeyValuePair<string, string>>
        {
            new("RoleName", roleName),
            new("Description", "دور اختبار آلي")
        };
        roleFields.AddRange(permissionIds.Select(id => new KeyValuePair<string, string>("PermissionIds", id.ToString())));

        var roleResponse = await admin.PostFormAsync("/Roles/Create", "/Roles/Create", roleFields);
        roleResponse.EnsureSuccessStatusCode();
        var roleId = await _fixture.Factory.RoleIdAsync(roleName);

        var userFields = new List<KeyValuePair<string, string>>
        {
            new("Username", username),
            new("FullName", "مستخدم اختبار"),
            new("Email", $"{username}@example.com"),
            new("Phone", "0500000000"),
            new("Password", "User@12345"),
            new("RoleId", roleId.ToString())
        };
        var userResponse = await admin.PostFormAsync("/Users/Create", "/Users/Create", userFields);
        userResponse.EnsureSuccessStatusCode();

        return new TestUser(username, "User@12345");
    }

    private sealed record TestUser(string Username, string Password);
}
