using System.Net;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Regression tests for the defects found during the full-project QA pass. Each test pins a bug
/// that was fixed, so a future change that reintroduces it fails here instead of in production.
/// </summary>
[Collection(AppCollection.Name)]
public class QaRegressionTests
{
    private readonly AppFixture _fixture;

    public QaRegressionTests(AppFixture fixture) => _fixture = fixture;

    // BUG-1..4: posting a create form with empty required fields used to throw a 500
    // (DB "Column cannot be null"). The controller now returns the form with validation errors.
    [Theory]
    [InlineData("/Instructors/Create", "/Instructors/Create", "FullName")]
    [InlineData("/PostsManage/Create", "/PostsManage/Create", "Title")]
    [InlineData("/NewsManage/Create", "/NewsManage/Create", "Title")]
    [InlineData("/PhoneCountries/Create", "/PhoneCountries/Create", "CountryName")]
    public async Task Create_with_empty_required_field_shows_validation_not_500(
        string tokenPath, string action, string missingField)
    {
        var admin = await SignInAdminAsync();
        var fields = new List<KeyValuePair<string, string>> { new(missingField, string.Empty) };

        var response = await admin.PostFormAsync(tokenPath, action, fields);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("field-validation-error", html);
    }

    // BUG-5: several POST actions (HomepageManage, NewsManage) lacked [ValidateAntiForgeryToken],
    // so a cross-site form could mutate content. They now reject a request without a token.
    [Theory]
    [InlineData("/HomepageManage/AddAnimatedText", "text")]
    [InlineData("/HomepageManage/SaveTheme", "PrimaryColor")]
    [InlineData("/HomepageManage/ToggleSection", "id")]
    [InlineData("/NewsManage/Publish", "id")]
    [InlineData("/NewsManage/Delete", "id")]
    public async Task Post_without_antiforgery_token_is_rejected(string action, string field)
    {
        var admin = await SignInAdminAsync();

        // Post a valid-looking body but deliberately omit __RequestVerificationToken.
        var response = await admin.PostWithoutTokenAsync(action, new[] { new KeyValuePair<string, string>(field, "1") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // BUG-6: the slide edit button embedded admin-editable text into an inline onclick handler via
    // Html.Raw, so a crafted title broke out of the string and executed script. The data now travels
    // through HTML-encoded data-* attributes.
    [Fact]
    public async Task Slide_editor_does_not_emit_inline_onclick_handler()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/HomepageManage/Slides");

        Assert.DoesNotContain("onclick=\"editSlide(", html);
        Assert.Contains("data-slide-title=", html);
    }

    // BUG-9: the payments POST accepted a zero or negative amount and wrote it straight to the
    // invoice, driving PaidAmount negative. The amount is now validated.
    [Theory]
    [InlineData("0")]
    [InlineData("-100")]
    public async Task Payment_with_non_positive_amount_is_rejected(string amount)
    {
        var invoiceId = await _fixture.Factory.CreateInvoiceAsync();
        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync($"/Payments/Add?invoiceId={invoiceId}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", invoiceId.ToString()),
            new KeyValuePair<string, string>("Amount", amount),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("المبلغ يجب أن يكون أكبر من صفر", html);

        // The rejected payment must not have touched the invoice.
        var (paid, _) = await _fixture.Factory.GetInvoiceAsync(invoiceId);
        Assert.Equal(0m, paid);
    }

    // BUG-7/8: AcademicCatalog create actions threw a 500 when the name field was missing, because
    // NormalizeName dereferenced null. They now report a validation error and redirect.
    [Theory]
    [InlineData("/AcademicCatalog/CreateUniversity")]
    [InlineData("/AcademicCatalog/CreateCollege")]
    [InlineData("/AcademicCatalog/CreateSpecialization")]
    [InlineData("/AcademicCatalog/CreateLevel")]
    public async Task AcademicCatalog_create_with_missing_name_does_not_500(string action)
    {
        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/AcademicCatalog", action, Array.Empty<KeyValuePair<string, string>>());

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }
}
