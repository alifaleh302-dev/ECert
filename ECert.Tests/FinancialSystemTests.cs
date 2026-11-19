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

    /// <summary>
    /// A receipt shows the centre's uploaded logo when one is configured, so the voucher carries the
    /// centre's identity. The logo is read from the same SiteSettings the layout uses.
    /// </summary>
    [Fact]
    public async Task The_receipt_shows_the_centre_logo_when_one_is_configured()
    {
        const string logo = "/uploads/site/logo-test.png";
        await _fixture.Factory.SetSettingAsync("LogoUrl", logo);
        try
        {
            var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
            var admin = await SignInAdminAsync();
            var response = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
            {
                new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
                new KeyValuePair<string, string>("Amount", "150"),
                new KeyValuePair<string, string>("PaymentMethod", "Cash")
            });
            response.EnsureSuccessStatusCode();
            var receiptPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;

            var html = await admin.GetStringAsync(receiptPath);
            Assert.Contains("rp-sheet__logo", html);
            Assert.Contains(logo, html);
        }
        finally
        {
            // The fixture database is shared, so clear the logo or it leaks into later receipts.
            await _fixture.Factory.SetSettingAsync("LogoUrl", string.Empty);
        }
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

    // ===== Refund entry points: the refunds page =====

    /// <summary>
    /// The payments list offers a print-receipt button on each row that opens the payment's own
    /// receipt (سند القبض) in a new tab, so a receipt can be reprinted straight from the payment.
    /// Refunds are no longer started from here — they begin on the refunds page.
    /// </summary>
    [Fact]
    public async Task The_payments_tab_offers_a_print_receipt_button_for_each_payment()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "200"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        var html = await admin.GetStringAsync("/Payments");
        var paymentId = (await _fixture.Factory.LatestPaymentAsync(invoiceId))!.PaymentId;
        Assert.Contains($"/Payments/Receipt/{paymentId}", html);
        // The old refund shortcut is gone from the payments tab.
        Assert.DoesNotContain($"/Refunds/Create?invoiceId={invoiceId}", html);
    }

    /// <summary>
    /// The refunds page now opens an invoice picker, so a refund can be started without first
    /// locating the invoice elsewhere. Only invoices with a positive refundable balance appear.
    /// </summary>
    [Fact]
    public async Task The_refunds_page_links_to_a_refund_invoice_picker()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Refunds");
        Assert.Contains("/Refunds/NewRefund", html);
    }

    [Fact]
    public async Task The_refund_invoice_picker_lists_a_paid_invoice_and_omits_an_unpaid_one()
    {
        var paidInvoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var unpaidInvoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        await admin.PostFormAsync($"/Payments/Add?invoiceId={paidInvoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", paidInvoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "300"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        var html = await admin.GetStringAsync("/Refunds/NewRefund");
        Assert.Contains($"/Refunds/Create?invoiceId={paidInvoiceId}", html);
        Assert.DoesNotContain($"/Refunds/Create?invoiceId={unpaidInvoiceId}", html);
    }

    /// <summary>
    /// The refunds list no longer offers a further-refund button on a row: a new refund is started from
    /// the invoice picker (طلب استرداد جديد), not by re-refunding a row already shown in the list.
    /// </summary>
    [Fact]
    public async Task The_refunds_list_no_longer_offers_a_further_refund_button()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "500"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        await admin.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "100"),
            new KeyValuePair<string, string>("reason", "استرداد جزئي"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });

        var html = await admin.GetStringAsync("/Refunds");
        // The refund still appears in the list (with its receipt link), but not a further-refund button.
        Assert.Contains($"/Refunds/Receipt/", html);
        Assert.DoesNotContain($"/Refunds/Create?invoiceId={invoiceId}", html);
    }

    /// <summary>
    /// A pending refund request can be withdrawn by the employee who created it — the row shows an
    /// إلغاء button only to the requester, so they can pull the request back before it is approved.
    /// </summary>
    [Fact]
    public async Task The_refunds_list_offers_a_cancel_button_to_the_requester_of_a_pending_refund()
    {
        // A 20,000 refund on a 20,000 collection is above the default ceiling, so it waits for approval.
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(20_000m);
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds, AdminPermissions.ViewCustody
        });
        var client = await SignInAsync(collector.Username, collector.Password);

        await client.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "20000"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        await client.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        var refundId = (await _fixture.Factory.LatestRefundAsync(invoiceId))!.RefundId;
        Assert.Equal(RefundStatus.PendingApproval, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);

        var html = await client.GetStringAsync("/Refunds");
        Assert.Contains($"/Refunds/Cancel/{refundId}", html);
    }

    /// <summary>
    /// Cancelling a pending refund request marks it cancelled and releases the custody it was reserving,
    /// so the employee's balance is free again — and the invoice is untouched because nothing moved.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_pending_refund_releases_the_reserved_custody()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(20_000m);
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds, AdminPermissions.ViewCustody
        });
        var client = await SignInAsync(collector.Username, collector.Password);
        var collectorId = await _fixture.Factory.UserIdAsync(collector.Username);

        await client.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "20000"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        Assert.Equal(20_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));

        await client.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        var refundId = (await _fixture.Factory.LatestRefundAsync(invoiceId))!.RefundId;
        Assert.Equal(RefundStatus.PendingApproval, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);

        var cancel = await client.PostFormAsync("/Refunds", $"/Refunds/Cancel/{refundId}", new[]
        {
            new KeyValuePair<string, string>("id", refundId.ToString())
        });
        cancel.EnsureSuccessStatusCode();

        Assert.Equal(RefundStatus.Cancelled, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);
        // Nothing moved: the collection stays in the custody and the invoice keeps its paid amount.
        Assert.Equal(20_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
        Assert.Equal(20_000m, (await _fixture.Factory.GetInvoiceAsync(invoiceId)).PaidAmount);
    }

    /// <summary>
    /// A pending refund may only be withdrawn by the employee who requested it: a different user (even
    /// one who may approve refunds) cannot cancel it from the list, and the request stays pending.
    /// </summary>
    [Fact]
    public async Task A_pending_refund_cannot_be_cancelled_by_someone_other_than_the_requester()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(20_000m);
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds, AdminPermissions.ViewCustody
        });
        var collectorClient = await SignInAsync(collector.Username, collector.Password);

        await collectorClient.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "20000"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        await collectorClient.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        var refundId = (await _fixture.Factory.LatestRefundAsync(invoiceId))!.RefundId;

        // The SuperAdmin may approve refunds but is not the requester, so the row shows no cancel button.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Refunds");
        Assert.DoesNotContain($"/Refunds/Cancel/{refundId}", html);

        // And a crafted POST is refused, leaving the request pending.
        var response = await admin.PostFormAsync("/Refunds", $"/Refunds/Cancel/{refundId}", new[]
        {
            new KeyValuePair<string, string>("id", refundId.ToString())
        });
        response.EnsureSuccessStatusCode();
        Assert.Equal(RefundStatus.PendingApproval, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);
    }

    /// <summary>A refund started from the picker reaches the same working form and moves the money.</summary>
    [Fact]
    public async Task A_refund_started_from_the_picker_form_reduces_the_invoice()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "500"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        // The picker links to /Refunds/Create?invoiceId=…; follow it and confirm the form renders.
        var picker = await admin.GetStringAsync("/Refunds/NewRefund");
        Assert.Contains($"/Refunds/Create?invoiceId={invoiceId}", picker);
        var formHtml = await admin.GetStringAsync($"/Refunds/Create?invoiceId={invoiceId}");
        Assert.Contains("سند استرداد جديد", System.Net.WebUtility.HtmlDecode(formHtml));

        var response = await admin.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "120"),
            new KeyValuePair<string, string>("reason", "انسحاب من الدورة"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal(120m, await _fixture.Factory.RefundedTotalAsync(invoiceId));
        Assert.Equal(380m, (await _fixture.Factory.GetInvoiceAsync(invoiceId)).PaidAmount);
    }

    /// <summary>The picker is gated on manage-refunds: a user without it cannot open it.</summary>
    [Fact]
    public async Task The_refund_invoice_picker_is_gated_on_manage_refunds()
    {
        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ManagePayments });
        var client = await SignInAsync(user.Username, user.Password);

        Assert.True(await client.CanOpenAsync("/Payments"));
        Assert.False(await client.CanOpenAsync("/Refunds/NewRefund"));
    }

    /// <summary>
    /// The print-receipt button on the payments list is available to anyone who may view payments,
    /// and it links to that row's own receipt (not to a refund form, which now lives on the refunds page).
    /// </summary>
    [Fact]
    public async Task The_print_receipt_button_follows_the_view_payment_permission()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "150"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        var paymentId = (await _fixture.Factory.LatestPaymentAsync(invoiceId))!.PaymentId;

        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ViewPayments });
        var client = await SignInAsync(user.Username, user.Password);

        var html = await client.GetStringAsync("/Payments");
        Assert.Contains($"/Payments/Receipt/{paymentId}", html);
        Assert.DoesNotContain("/Refunds/Create?invoiceId=", html);
    }

    // ===== Refund approval funding + competing custody claims =====

    /// <summary>
    /// A refund above the ceiling waits for approval. When it is approved it must debit the custody of
    /// the employee who requested it — that is where the collected money sits — not the approver's
    /// custody. A SuperAdmin who holds no custody must still be able to approve, and the requester's
    /// balance must drop by the refund amount.
    /// </summary>
    [Fact]
    public async Task Approving_a_pending_refund_debits_the_requesters_custody_not_the_approvers()
    {
        // A collector records 20,000 (custody +20,000). The ceiling is 500, so a 20,000 refund waits.
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(20_000m);
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds, AdminPermissions.ViewCustody
        });
        var collectorClient = await SignInAsync(collector.Username, collector.Password);
        var collectorId = await _fixture.Factory.UserIdAsync(collector.Username);

        var collect = await collectorClient.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "20000"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        collect.EnsureSuccessStatusCode();
        Assert.Equal(20_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));

        var refund = await collectorClient.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        refund.EnsureSuccessStatusCode();
        Assert.Equal(RefundStatus.PendingApproval, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);

        // The SuperAdmin approves; they hold no custody of their own and the fund is empty.
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();
        var adminCustodyBefore = await _fixture.Factory.CustodyBalanceAsync(adminId);

        var refundId = (await _fixture.Factory.LatestRefundAsync(invoiceId))!.RefundId;
        var approve = await admin.PostFormAsync("/Refunds", $"/Refunds/Approve/{refundId}", new[]
        {
            new KeyValuePair<string, string>("id", refundId.ToString())
        });
        approve.EnsureSuccessStatusCode();

        var approved = await _fixture.Factory.LatestRefundAsync(invoiceId);
        Assert.Equal(RefundStatus.Approved, approved!.Status);
        Assert.False(approved.PaidFromFund);
        // The requester's custody paid the refund; the approver's custody is untouched.
        Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
        Assert.Equal(adminCustodyBefore, await _fixture.Factory.CustodyBalanceAsync(adminId));
        Assert.Equal(0m, (await _fixture.Factory.GetInvoiceAsync(invoiceId)).PaidAmount);
    }

    /// <summary>
    /// Two operations must not claim the same custody money. While a 20,000 refund is pending
    /// approval it is a claim against the requester's 20,000 custody, so the requester cannot also
    /// hand over 20,000 — otherwise approving the refund would drive the custody negative.
    /// </summary>
    [Fact]
    public async Task A_pending_refund_reserves_the_custody_so_a_handover_cannot_over_claim_it()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(20_000m);
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds,
            AdminPermissions.ViewCustody, AdminPermissions.ApproveCustody
        });
        var client = await SignInAsync(collector.Username, collector.Password);
        var collectorId = await _fixture.Factory.UserIdAsync(collector.Username);

        await client.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "20000"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        Assert.Equal(20_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));

        // A 20,000 refund waits for approval, reserving the whole custody.
        await client.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        Assert.Equal(RefundStatus.PendingApproval, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);

        // Handing over the same 20,000 must be refused: it is already claimed by the pending refund.
        var handover = await client.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
        {
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("note", "توريد كامل العهدة")
        });
        handover.EnsureSuccessStatusCode();
        Assert.Null(await _fixture.Factory.LatestHandoverStatusAsync(collectorId));

        // The refund can still be approved, and the custody lands exactly at zero — never negative.
        var refundId = (await _fixture.Factory.LatestRefundAsync(invoiceId))!.RefundId;
        var admin = await SignInAdminAsync();
        await admin.PostFormAsync("/Refunds", $"/Refunds/Approve/{refundId}", new[]
        {
            new KeyValuePair<string, string>("id", refundId.ToString())
        });
        Assert.Equal(RefundStatus.Approved, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);
        Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
    }

    /// <summary>
    /// The reverse order of the race: a handover request also reserves the custody, so a refund
    /// cannot claim money that a pending handover already set aside.
    /// </summary>
    [Fact]
    public async Task A_pending_handover_reserves_the_custody_so_a_refund_cannot_over_claim_it()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(20_000m);
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds,
            AdminPermissions.ViewCustody, AdminPermissions.ApproveCustody
        });
        var client = await SignInAsync(collector.Username, collector.Password);
        var collectorId = await _fixture.Factory.UserIdAsync(collector.Username);

        await client.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "20000"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        // Hand over the whole 20,000 first; it now reserves the custody while pending.
        await client.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
        {
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("note", "توريد كامل العهدة")
        });
        Assert.Equal(CustodyHandoverStatus.Pending, await _fixture.Factory.LatestHandoverStatusAsync(collectorId));

        // A refund that would dip into the reserved money must be refused at creation.
        var refund = await client.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        refund.EnsureSuccessStatusCode();
        Assert.Null(await _fixture.Factory.LatestRefundAsync(invoiceId));
        // The custody is unchanged and still fully reserved for the handover.
        Assert.Equal(20_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
    }

    /// <summary>
    /// The reported scenario: an employee holds 22,000 of custody, hands over 2,000 to the fund, then
    /// refunds 20,000. Every step must succeed and the custody must land exactly at zero — never
    /// negative — with the fund receiving exactly the 2,000 that was handed over.
    /// </summary>
    [Fact]
    public async Task Scenario_custody_22000_handover_2000_then_refund_20000_settles_to_zero()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(22_000m);
        var admin = await SignInAdminAsync();
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds, AdminPermissions.ViewCustody
        });
        var client = await SignInAsync(collector.Username, collector.Password);
        var collectorId = await _fixture.Factory.UserIdAsync(collector.Username);
        var fundBefore = await _fixture.Factory.MainFundBalanceAsync();

        // The collector records the full 22,000 into their custody.
        await client.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "22000"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        Assert.Equal(22_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));

        // They hand over 2,000 to the main fund, which the SuperAdmin approves.
        await client.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
        {
            new KeyValuePair<string, string>("amount", "2000"),
            new KeyValuePair<string, string>("note", "توريد جزئي")
        });
        var handoverId = await _fixture.Factory.LatestHandoverIdAsync(collectorId);
        await admin.PostFormAsync("/Custody", $"/Custody/Approve/{handoverId}", new[]
        {
            new KeyValuePair<string, string>("id", handoverId.ToString())
        });
        Assert.Equal(20_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
        Assert.Equal(2_000m, await _fixture.Factory.MainFundBalanceAsync() - fundBefore);

        // A 20,000 refund is above the 500 ceiling, so it waits; the SuperAdmin approves it. The
        // remaining 20,000 of custody covers it exactly and it debits the requester, not the approver.
        await client.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
        {
            new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("amount", "20000"),
            new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
            new KeyValuePair<string, string>("refundMethod", "Cash")
        });
        var refundId = (await _fixture.Factory.LatestRefundAsync(invoiceId))!.RefundId;
        var approve = await admin.PostFormAsync("/Refunds", $"/Refunds/Approve/{refundId}", new[]
        {
            new KeyValuePair<string, string>("id", refundId.ToString())
        });
        approve.EnsureSuccessStatusCode();

        Assert.Equal(RefundStatus.Approved, (await _fixture.Factory.LatestRefundAsync(invoiceId))!.Status);
        Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
        // The trainee paid a net 22,000 − 20,000 refund = 2,000, which matches the fund's 2,000.
        Assert.Equal(2_000m, (await _fixture.Factory.GetInvoiceAsync(invoiceId)).PaidAmount);
        // The fund holds exactly the handed-over 2,000 — the refund did not touch it.
        Assert.Equal(2_000m, await _fixture.Factory.MainFundBalanceAsync() - fundBefore);
    }

    // ===== Financial system settings: automation + handover cap =====

    /// <summary>
    /// The settings page exposes the three new controls and a save round-trips each one, so an
    /// administrator can turn refund auto-approval and handover auto-approval on and cap the number of
    /// open handovers per employee.
    /// </summary>
    [Fact]
    public async Task Finance_settings_expose_and_save_the_automation_controls()
    {
        var admin = await SignInAdminAsync();

        var html = await admin.GetStringAsync("/FinanceSettings");
        Assert.Contains("SaveRefundAutoApproval", html);
        Assert.Contains("SaveHandoverAutoApproval", html);
        Assert.Contains("SaveMaxOpenHandovers", html);

        try
        {
            foreach (var (action, field, value) in new[]
            {
                ("SaveRefundAutoApproval", "refundAutoApproval", "true"),
                ("SaveHandoverAutoApproval", "handoverAutoApproval", "true"),
                ("SaveMaxOpenHandovers", "maxOpenHandovers", "3")
            })
            {
                var response = await admin.PostFormAsync("/FinanceSettings", $"/FinanceSettings/{action}", new[]
                {
                    new KeyValuePair<string, string>(field, value)
                });
                response.EnsureSuccessStatusCode();
            }

            Assert.Equal("true", await SettingAsync(SystemSettingsService.RefundAutoApprovalKey));
            Assert.Equal("true", await SettingAsync(SystemSettingsService.HandoverAutoApprovalKey));
            Assert.Equal("3", await SettingAsync(SystemSettingsService.MaxOpenHandoversPerEmployeeKey));
        }
        finally
        {
            await ResetAutomationSettingsAsync();
        }
    }

    /// <summary>
    /// With refund auto-approval on, a refund that would otherwise wait for approval applies at once:
    /// no pending row is left and the custody moves immediately.
    /// </summary>
    [Fact]
    public async Task Refund_auto_approval_applies_a_large_refund_immediately()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync(20_000m);
        var admin = await SignInAdminAsync();
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds
        });
        var collectorClient = await SignInAsync(collector.Username, collector.Password);
        var collectorId = await _fixture.Factory.UserIdAsync(collector.Username);

        try
        {
            // Turn the ceiling gate off, then refund well above the 500 ceiling.
            await admin.PostFormAsync("/FinanceSettings", "/FinanceSettings/SaveRefundAutoApproval", new[]
            {
                new KeyValuePair<string, string>("refundAutoApproval", "true")
            });
            await collectorClient.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
            {
                new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
                new KeyValuePair<string, string>("Amount", "20000"),
                new KeyValuePair<string, string>("PaymentMethod", "Cash")
            });
            Assert.Equal(20_000m, await _fixture.Factory.CustodyBalanceAsync(collectorId));

            await collectorClient.PostFormAsync($"/Refunds/Create?invoiceId={invoiceId}", "/Refunds/Create", new[]
            {
                new KeyValuePair<string, string>("invoiceId", invoiceId.ToString()),
                new KeyValuePair<string, string>("amount", "20000"),
                new KeyValuePair<string, string>("reason", "انسحاب المتدرب"),
                new KeyValuePair<string, string>("refundMethod", "Cash")
            });

            var refund = await _fixture.Factory.LatestRefundAsync(invoiceId);
            Assert.NotNull(refund);
            Assert.Equal(RefundStatus.Approved, refund!.Status);
            Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
            Assert.Equal(0m, (await _fixture.Factory.GetInvoiceAsync(invoiceId)).PaidAmount);
        }
        finally
        {
            await ResetAutomationSettingsAsync();
        }
    }

    /// <summary>
    /// With handover auto-approval on, a handover request settles at once: the custody is debited and
    /// the main fund credited without a separate approval step.
    /// </summary>
    [Fact]
    public async Task Handover_auto_approval_settles_the_request_immediately()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var collector = await CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManagePayments, AdminPermissions.ViewCustody
        });
        var collectorClient = await SignInAsync(collector.Username, collector.Password);
        var collectorId = await _fixture.Factory.UserIdAsync(collector.Username);
        var fundBefore = await _fixture.Factory.MainFundBalanceAsync();

        try
        {
            await admin.PostFormAsync("/FinanceSettings", "/FinanceSettings/SaveHandoverAutoApproval", new[]
            {
                new KeyValuePair<string, string>("handoverAutoApproval", "true")
            });
            await collectorClient.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
            {
                new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
                new KeyValuePair<string, string>("Amount", "400"),
                new KeyValuePair<string, string>("PaymentMethod", "Cash")
            });

            var response = await collectorClient.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
            {
                new KeyValuePair<string, string>("amount", "400"),
                new KeyValuePair<string, string>("note", "توريد تلقائي")
            });
            response.EnsureSuccessStatusCode();

            // It was approved on the spot: no request is left pending.
            Assert.Equal(CustodyHandoverStatus.Approved, await _fixture.Factory.LatestHandoverStatusAsync(collectorId));
            Assert.Equal(0, await _fixture.Factory.PendingHandoverCountAsync(collectorId));
            Assert.Equal(0m, await _fixture.Factory.CustodyBalanceAsync(collectorId));
            Assert.Equal(400m, await _fixture.Factory.MainFundBalanceAsync() - fundBefore);
        }
        finally
        {
            await ResetAutomationSettingsAsync();
        }
    }

    /// <summary>
    /// The handover cap is enforced: with a cap of 1, a second open request is refused while the first
    /// is still pending.
    /// </summary>
    [Fact]
    public async Task The_handover_cap_limits_open_requests_per_employee()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var adminId = await _fixture.Factory.AdminUserIdAsync();

        await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "500"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        // First request is accepted and stays pending.
        await admin.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
        {
            new KeyValuePair<string, string>("amount", "100"),
            new KeyValuePair<string, string>("note", "أول طلب")
        });
        Assert.Equal(1, await _fixture.Factory.PendingHandoverCountAsync(adminId));

        // A second request is refused while the first is open (default cap 1).
        await admin.PostFormAsync("/Custody", "/Custody/RequestHandover", new[]
        {
            new KeyValuePair<string, string>("amount", "100"),
            new KeyValuePair<string, string>("note", "ثاني طلب")
        });
        Assert.Equal(1, await _fixture.Factory.PendingHandoverCountAsync(adminId));

        // Leave no pending request behind: it would keep reserving the admin's custody for later tests.
        await _fixture.Factory.ClearPendingHandoversAsync(adminId);
    }

    private Task<string> SettingAsync(string key) =>
        _fixture.Factory.WithDbAsync(async db =>
        {
            var setting = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.SiteSettings.Where(s => s.Key == key));
            return setting?.Value ?? string.Empty;
        });

    /// <summary>
    /// Restores the automation settings to their defaults. The settings live in the shared fixture
    /// database, so a test that turns one on must turn it back off or it leaks into later tests.
    /// </summary>
    private async Task ResetAutomationSettingsAsync()
    {
        await _fixture.Factory.SetSettingAsync(SystemSettingsService.RefundAutoApprovalKey, "false");
        await _fixture.Factory.SetSettingAsync(SystemSettingsService.HandoverAutoApprovalKey, "false");
        await _fixture.Factory.SetSettingAsync(SystemSettingsService.MaxOpenHandoversPerEmployeeKey, "1");
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
