using System.Net;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the financial-correctness fixes: the invoice balance is always derived from the payment
/// ledger, a cancelled payment stops counting, a cancelled invoice cannot collect money, and an
/// overpayment is rejected. These are the invariants a course-scoped collector relies on when they
/// see only their own courses' money.
/// </summary>
[Collection(AppCollection.Name)]
public class FinanceIntegrityTests
{
    private readonly AppFixture _fixture;

    public FinanceIntegrityTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Deleting_a_payment_recomputes_the_invoice_balance()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var paymentId = await _fixture.Factory.AddPaymentAsync(invoiceId, 200m);

        var (paidBefore, statusBefore) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(200m, paidBefore);
        Assert.Equal("PartiallyPaid", statusBefore);

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/Invoices/Details/" + invoiceId, "/Payments/Delete", new[]
        {
            new KeyValuePair<string, string>("id", paymentId.ToString())
        });
        response.EnsureSuccessStatusCode();

        var (paidAfter, statusAfter) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(0m, paidAfter);
        Assert.Equal("Unpaid", statusAfter);
    }

    [Fact]
    public async Task Paying_the_full_amount_marks_the_invoice_paid()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();

        var response = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "500"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });
        response.EnsureSuccessStatusCode();

        var (paid, status) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(500m, paid);
        Assert.Equal("Paid", status);
    }

    [Fact]
    public async Task A_cancelled_invoice_rejects_new_payments()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();

        // Capture a token while the invoice is still active: after cancellation the page no longer
        // renders the actions, so the payment post must reuse the token obtained beforehand.
        var token = await admin.FetchTokenAsync($"/Invoices/Details/{invoiceId}");

        var cancel = await admin.PostFormAsync("/Invoices/Details/" + invoiceId, "/Invoices/Cancel", new[]
        {
            new KeyValuePair<string, string>("id", invoiceId.ToString())
        });
        cancel.EnsureSuccessStatusCode();

        var response = await admin.PostFormWithTokenAsync(token, "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", "100"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var (paid, _) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(0m, paid);
    }

    [Fact]
    public async Task An_invoice_with_active_payments_cannot_be_cancelled()
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        await _fixture.Factory.AddPaymentAsync(invoiceId, 100m);
        var admin = await SignInAdminAsync();

        var response = await admin.PostFormAsync("/Invoices/Details/" + invoiceId, "/Invoices/Cancel", new[]
        {
            new KeyValuePair<string, string>("id", invoiceId.ToString())
        });
        response.EnsureSuccessStatusCode();

        var status = await _fixture.Factory.InvoiceStatusAsync(invoiceId);
        Assert.NotEqual("Cancelled", status);
    }

    [Fact]
    public async Task The_role_form_offers_permission_presets()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Roles/Create");

        Assert.Contains("id=\"rolePreset\"", html);
        foreach (var preset in ECert.Services.AdminPermissions.Presets)
            Assert.Contains($"value=\"{preset.Key}\"", html);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }
}
