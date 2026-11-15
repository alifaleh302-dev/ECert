using System.Text.RegularExpressions;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// End-to-end tests for the certificate designer, driven through the real HTTP surface: the name
/// input must actually reach the save action, and deleting a design must respect the guards that
/// keep the published design (and any design a course still points at) alive.
/// </summary>
[Collection(AppCollection.Name)]
public class CertificateDesignerTests
{
    private readonly AppFixture _fixture;

    public CertificateDesignerTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Editor_shows_the_saved_name_and_wires_the_input_to_the_form()
    {
        // Regression: the name input sat outside #templateForm and lacked form="templateForm", so a
        // rename silently kept the old name — the input never posted.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        var input = Regex.Match(html, "<input[^>]*id=\"templateName\"[^>]*>");
        Assert.True(input.Success, "the editor should render the template-name input");
        Assert.Contains("form=\"templateForm\"", input.Value);
        Assert.Contains("name=\"Name\"", input.Value);
    }

    [Fact]
    public async Task Saving_a_new_name_persists_it()
    {
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة قبل التسمية");

        var name = $"اسم محدّث {Guid.NewGuid():N}"[..24];
        var response = await admin.PostFormAsync("/CertificateDesigns", "/CertificateDesigns/Save", new Dictionary<string, string>
        {
            ["CertificateDesignId"] = designId.ToString(),
            ["Name"] = name,
            ["CanvasWidth"] = "1120",
            ["CanvasHeight"] = "792",
            ["BackgroundColor"] = "#fffdf7",
            ["BorderColor"] = "#c9a227",
            ["BorderWidth"] = "12",
            ["BorderRadius"] = "8",
            ["ElementsJson"] = "[]",
            ["Publish"] = "false"
        });
        response.EnsureSuccessStatusCode();

        var stored = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Single(d => d.CertificateDesignId == designId).Name));
        Assert.Equal(name, stored);
    }

    [Fact]
    public async Task Delete_removes_a_draft_and_its_elements()
    {
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة قابلة للحذف");
        await _fixture.Factory.WithDbAsync(async db =>
        {
            db.CertificateDesignElements.Add(new CertificateDesignElement
            {
                CertificateDesignId = designId,
                ElementType = "field",
                FieldKey = "trainee_name"
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await admin.PostFormAsync("/CertificateDesigns", $"/CertificateDesigns/Delete/{designId}", new Dictionary<string, string>());
        response.EnsureSuccessStatusCode();

        var remaining = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Count(d => d.CertificateDesignId == designId)));
        Assert.Equal(0, remaining);

        var orphanedElements = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesignElements.Count(e => e.CertificateDesignId == designId)));
        Assert.Equal(0, orphanedElements);
    }

    [Fact]
    public async Task Delete_refuses_the_published_design()
    {
        var admin = await SignInAdminAsync();
        var publishedId = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Where(d => d.IsPublished).Select(d => d.CertificateDesignId).First()));

        var response = await admin.PostFormAsync("/CertificateDesigns", $"/CertificateDesigns/Delete/{publishedId}", new Dictionary<string, string>());
        response.EnsureSuccessStatusCode();

        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Any(d => d.CertificateDesignId == publishedId))),
            "the published design must survive a delete request");
    }

    [Fact]
    public async Task Delete_refuses_a_design_a_course_still_uses()
    {
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة مرتبطة بدورة");
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة {Guid.NewGuid():N}"[..20]);
        await _fixture.Factory.ExecuteRawAsync($"UPDATE Courses SET CertificateDesignId = {designId} WHERE CourseId = {courseId};");

        var response = await admin.PostFormAsync("/CertificateDesigns", $"/CertificateDesigns/Delete/{designId}", new Dictionary<string, string>());
        response.EnsureSuccessStatusCode();

        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Any(d => d.CertificateDesignId == designId))),
            "a design a course points at must survive a delete request");
    }

    [Fact]
    public async Task Delete_without_an_antiforgery_token_is_rejected()
    {
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة ومحاولة CSRF");

        var response = await admin.PostWithoutTokenAsync($"/CertificateDesigns/Delete/{designId}", new Dictionary<string, string>());

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Any(d => d.CertificateDesignId == designId))),
            "a CSRF attempt must not delete anything");
    }

    [Fact]
    public async Task The_delete_button_is_hidden_for_the_published_design()
    {
        var admin = await SignInAdminAsync();
        var publishedId = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Where(d => d.IsPublished).Select(d => d.CertificateDesignId).First()));

        var html = await admin.GetStringAsync($"/CertificateDesigns?id={publishedId}");
        Assert.DoesNotContain($"/CertificateDesigns/Delete/{publishedId}", html);
    }

    private async Task<int> CreateDesignAsync(string name)
    {
        var id = await _fixture.Factory.WithDbAsync(async db =>
        {
            var design = new CertificateDesign
            {
                Name = name,
                DesignKey = $"draft-{Guid.NewGuid():N}",
                IsPublished = false,
                UpdatedBy = "test"
            };
            db.CertificateDesigns.Add(design);
            await db.SaveChangesAsync();
            return design.CertificateDesignId;
        });
        return id;
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }
}
