using System.Net;
using System.Text.RegularExpressions;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the single feedback channel: a server flash message must be queued once (rendered as a
/// toast by notify.js), never duplicated by a page that also reads TempData itself, and the toast
/// host must exist with the z-index token that keeps it above every overlay.
/// </summary>
[Collection(AppCollection.Name)]
public class NotificationTests
{
    private readonly AppFixture _fixture;

    public NotificationTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Flash_message_is_queued_once_and_not_rendered_twice()
    {
        // Regression: the layout and 13 views both rendered TempData, so a success message
        // appeared twice on the page.
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("قالب لاختبار الإشعار");

        var response = await admin.PostFormAsync("/CertificateDesigns",
            $"/CertificateDesigns/Rename/{designId}",
            new Dictionary<string, string> { ["name"] = "قالب أُعيدت تسميته" });

        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // Exactly one queue is emitted, and it carries the message.
        Assert.Single(Regex.Matches(html, "window\\.__ecertFlash"));
        Assert.Contains("تمت إعادة تسمية القالب", html);

        // The success alert may only appear inside the <noscript> fallback, never as a second
        // visible banner. CertificateDesigns/Index used to render its own copy.
        var noscript = Regex.Match(html, "<noscript>(.*?)</noscript>", RegexOptions.Singleline);
        var outsideNoscript = html.Replace(noscript.Value, string.Empty);
        Assert.DoesNotContain("alert alert-success", outsideNoscript);
    }

    [Fact]
    public async Task Every_page_carries_the_shared_toast_host_and_z_index_token()
    {
        var admin = await SignInAdminAsync();

        foreach (var path in new[] { "/Dashboard", "/CertificateDesigns", "/Cashbox" })
        {
            var html = await admin.GetStringAsync(path);
            Assert.Contains("notify.js", html);
            Assert.Contains("feedback.css", html);
        }

        var css = await admin.GetStringAsync("/css/feedback.css");
        Assert.Contains(".toast-host", css);
        Assert.Contains("--z-toast", css);
        // The toast layer must sit above the designer's fullscreen overlay.
        var toastZ = int.Parse(Regex.Match(css, @"--z-toast:\s*(\d+)").Groups[1].Value);
        var adminCss = await admin.GetStringAsync("/css/admin.css");
        var designerZ = int.Parse(Regex.Match(adminCss, @"--z-designer:\s*(\d+)").Groups[1].Value);
        Assert.True(toastZ > designerZ, "the toast host must sit above the designer overlay");
    }

    [Fact]
    public async Task Public_pages_load_the_shared_feedback_stylesheet()
    {
        // Regression: the public layout loaded only site.css, which carries neither the toast
        // styling nor the --a-* tokens the feedback layer needs, so a validation warning on the
        // public registration form (e.g. a bad phone number) rendered invisible.
        var courseId = await _fixture.Factory.FirstOpenCourseIdAsync();
        var client = AppClient.Create(_fixture.Factory);

        var html = await client.GetStringAsync($"/Registration/Register?courseId={courseId}");
        Assert.Contains("feedback.css", html);
        Assert.Contains("notify.js", html);

        var css = await client.GetStringAsync("/css/feedback.css");
        Assert.Contains(".toast-host__item", css);
        Assert.Contains("--a-surface", css);
    }

    [Fact]
    public async Task Standalone_auth_pages_can_render_feedback()
    {
        // Regression: Login and VerifyTwoFactor set `Layout = null`, so they bypass both layouts.
        // When the toast CSS moved into feedback.css they lost it, and Login — the page that
        // receives the "password updated" flash — had no notify.js either, so that message
        // vanished. These pages must carry the shared feedback assets themselves.
        var client = AppClient.Create(_fixture.Factory);
        var html = await client.GetStringAsync("/Auth/Login");

        Assert.Contains("feedback.css", html);
        Assert.Contains("notify.js", html);
    }

    [Fact]
    public async Task A_page_without_a_flash_renders_no_alert_banner()
    {
        // Cashbox/Index used to render its own TempData banner. A plain visit carries no flash,
        // so the page must show nothing at all — no queue and no banner.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Cashbox");
        Assert.DoesNotContain("window.__ecertFlash", html);
        Assert.DoesNotContain("a-note--success", html);
        Assert.DoesNotContain("a-note--danger", html);
    }

    [Fact]
    public async Task Destructive_actions_use_the_shared_confirm_dialog_not_native_confirm()
    {
        // Phase 4: inline onclick/onsubmit="return confirm(...)" were replaced by data-confirm
        // so the on-brand Notify dialog owns every confirmation.
        var admin = await SignInAdminAsync();

        foreach (var path in new[] { "/CertificateDesigns", "/Invoices", "/Registrations", "/CategoriesManage" })
        {
            var html = await admin.GetStringAsync(path);
            Assert.DoesNotContain("return confirm(", html);
            Assert.DoesNotContain("window.confirm", html);
        }
    }

    [Fact]
    public async Task Data_confirm_marks_destructive_actions_with_the_danger_tone()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Invoices");

        Assert.Contains("data-confirm=", html);
        // Cancelling an invoice is destructive, so it must opt into the danger styling.
        Assert.Contains("data-confirm-tone=\"danger\"", html);
    }

    [Fact]
    public async Task Notify_is_exposed_on_window_so_the_shared_guards_hold()
    {
        // Regression: notify.js declared `const Notify` at top-level, which does NOT create a
        // window property. site.js and api.js guard with `if (window.Notify)`, so both saw
        // undefined and fell through to their native confirm()/toast fallbacks — a click on a
        // destructive action fired the on-brand dialog AND the browser's native confirm().
        var admin = await SignInAdminAsync();
        var js = await admin.GetStringAsync("/js/notify.js");
        Assert.Contains("window.Notify = Notify", js);
    }

    [Fact]
    public async Task Toast_styling_is_namespaced_so_bootstrap_cannot_hide_it()
    {
        // Regression: the toast used Bootstrap's own `.toast` class. bootstrap.rtl.min.css ships
        // `.toast:not(.show){display:none}` (specificity 0,2,0) which beats the app's
        // `.toast{display:flex}` (0,1,0) — and notify.js never adds `.show`, so every success
        // and error notification rendered invisible. The item class is namespaced under the host
        // so no third-party `.toast` rule can reach it.
        var admin = await SignInAdminAsync();
        var js = await admin.GetStringAsync("/js/notify.js");
        var css = await admin.GetStringAsync("/css/feedback.css");

        Assert.Contains("toast-host__item", js);
        Assert.DoesNotContain("`toast toast--", js);
        Assert.Contains(".toast-host__item", css);
        Assert.Contains(".toast-host__item--success", css);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<int> CreateDesignAsync(string name)
    {
        return await _fixture.Factory.WithDbAsync(async db =>
        {
            var design = new CertificateDesign
            {
                Name = name,
                DesignKey = $"flash-{Guid.NewGuid():N}",
                IsPublished = false,
                UpdatedBy = "test"
            };
            db.CertificateDesigns.Add(design);
            await db.SaveChangesAsync();
            return design.CertificateDesignId;
        });
    }
}
