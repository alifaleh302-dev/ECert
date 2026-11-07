using System.Net;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// End-to-end tests for the public course registration form. They pin the fields the form must
/// (and must not) collect, and prove the submitted request is persisted with the country fixed
/// to Yemen even though the visitor never chooses one.
/// </summary>
[Collection(AppCollection.Name)]
public class PublicRegistrationFormTests
{
    private readonly AppFixture _fixture;

    public PublicRegistrationFormTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Form_omits_email_heard_from_and_country_picker_and_defaults_to_yemen()
    {
        var courseId = await _fixture.Factory.FirstOpenCourseIdAsync();
        var client = AppClient.Create(_fixture.Factory);

        var html = await client.GetStringAsync($"/Registration/Register?courseId={courseId}");

        // Removed fields.
        Assert.DoesNotContain("name=\"Email\"", html);
        Assert.DoesNotContain("name=\"HeardFrom\"", html);
        Assert.DoesNotContain("id=\"countrySelect\"", html);

        // Country is shown read-only as Yemen with its dial code. Razor escapes '+' in the dial
        // code, so compare against what the browser actually shows.
        var visible = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("اليمن", visible);
        Assert.Contains("+967", visible);
        Assert.Contains("readonly", visible);

        // The fields that must remain.
        Assert.Contains("name=\"FullNameArabic\"", html);
        Assert.Contains("name=\"FullNameEnglish\"", html);
        Assert.Contains("name=\"Phone\"", html);
    }

    [Fact]
    public async Task Post_without_country_email_or_heardfrom_persists_registration_for_yemen()
    {
        var courseId = await _fixture.Factory.FirstOpenCourseIdAsync();
        var client = AppClient.Create(_fixture.Factory);

        var response = await client.PostFormAsync(
            $"/Registration/Register?courseId={courseId}",
            "/Registration/Register",
            new Dictionary<string, string>
            {
                ["CourseId"] = courseId.ToString(),
                ["FullNameArabic"] = "طالب اختبار",
                ["FullNameEnglish"] = "Test Student",
                ["Gender"] = "Male",
                ["Phone"] = "771234567"
            });

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("تم استلام طلبك", html);

        // The visitor never picked a country, yet the number is stored with the Yemeni dial code.
        var registration = await _fixture.Factory.FindRegistrationByPhoneAsync("+967771234567");
        Assert.NotNull(registration);
        Assert.Equal("Pending", registration!.Status);
        Assert.Null(registration.Email);
        Assert.Null(registration.HeardFrom);
    }

    [Fact]
    public async Task Post_with_phone_from_another_country_is_rejected()
    {
        var courseId = await _fixture.Factory.FirstOpenCourseIdAsync();
        var client = AppClient.Create(_fixture.Factory);

        // A Saudi prefix (50…) is not a valid Yemeni mobile prefix.
        var response = await client.PostFormAsync(
            $"/Registration/Register?courseId={courseId}",
            "/Registration/Register",
            new Dictionary<string, string>
            {
                ["CourseId"] = courseId.ToString(),
                ["FullNameArabic"] = "طالب اختبار",
                ["FullNameEnglish"] = "Test Student",
                ["Gender"] = "Female",
                ["Phone"] = "501234567"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("البادئات", html);
        Assert.Null(await _fixture.Factory.FindRegistrationByPhoneAsync("+967501234567"));
    }
}
