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
    public async Task Editor_uses_the_enterprise_palette_without_leaking_bootstrap_colours()
    {
        // The designer must look like the rest of the enterprise console: one accent colour, no
        // Bootstrap colour utilities (text-success/text-primary) bleeding non-theme colours in, and
        // status rendered as quiet text rather than coloured chips.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.DoesNotContain("text-success", html);
        Assert.DoesNotContain("text-primary", html);
        Assert.Contains("ds-i-accent", html);
        Assert.Contains("ped-badge", html);
        Assert.DoesNotContain("background:#e7edff", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains("--ped-accent", css);
        Assert.DoesNotContain("--ped-ok", css);
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

    [Fact]
    public async Task Editor_exposes_professional_navigation_affordances()
    {
        // Phase 1 of the usability plan: the editor must behave like a professional canvas tool —
        // zoom around the pointer with Ctrl+wheel, pan with space/middle-drag, fit/selection zoom,
        // distance measurement guides, Ctrl to suspend snapping, and Shift to lock aspect ratio.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("id=\"measureLayer\"", html);
        Assert.Contains("id=\"zoomSelection\"", html);
        Assert.Contains("id=\"zoomLabel\"", html);
        Assert.Contains("wheel", html);
        Assert.Contains("zoomAround(", html);
        Assert.Contains("pan-ready", html);
        Assert.Contains("renderMeasures(", html);
        Assert.Contains("nearestEdges(", html);
        // Ctrl/Cmd must be able to temporarily suspend snapping.
        Assert.Contains("!ev.ctrlKey", html);
        // Shift must lock the aspect ratio while resizing from a corner.
        Assert.Contains("ev.shiftKey && orig.h > 0", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-measure-badge", css);
        Assert.Contains(".ped-measure-line", css);
        Assert.Contains(".ped-canvas-area.panning", css);
    }

    [Fact]
    public async Task Editor_bounds_zoom_to_a_sane_range()
    {
        // Zooming must stay inside a usable band; unbounded zoom either hides the artwork or
        // pixelates it beyond recognition. The viewport helpers clamp to [0.25, 3].
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("zoomAround(zoom * factor", html);
        Assert.Matches(@"clamp\(nextZoom, 0\.25, 3\)", html);
    }

    [Fact]
    public async Task Editor_supports_multi_selection_and_group_editing()
    {
        // Phase 2: the editor must select more than one element at a time — shift-click, a
        // rubber-band marquee, Ctrl+A — and move/resize/rotate the whole selection together.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("id=\"marqueeBox\"", html);
        Assert.Contains("function selectAll(", html);
        Assert.Contains("function marqueeSelect(", html);
        Assert.Contains("function startGroupResize(", html);
        Assert.Contains("function selectionBounds(", html);
        // Shift-click must extend the selection rather than replace it.
        Assert.Contains("select(i, e.shiftKey)", html);
        Assert.Contains("const additive = e.shiftKey;", html);
        // Ctrl+A selects everything.
        Assert.Contains("selectAll(); return;", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-marquee", css);
        Assert.Contains(".ped-group-box", css);
    }

    [Fact]
    public async Task Group_move_keeps_every_element_inside_the_canvas()
    {
        // A group drag must not push any member off-canvas; the bounds clamp the whole cluster.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("if (maxX > canvasW) ddx -= (maxX - canvasW);", html);
        Assert.Contains("if (maxY > canvasH) ddy -= (maxY - canvasH);", html);
    }

    [Fact]
    public async Task Editor_supports_alignment_distribution_and_layer_management()
    {
        // Phase 3: a professional editor aligns elements to the canvas or to each other, distributes
        // them evenly, reorders layers by drag, and can lock/hide/rename an element from the panel.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("function alignWithinSelection(", html);
        Assert.Contains("function distribute(", html);
        Assert.Contains("function tidyUp(", html);
        // Selection-relative tools are wired to the toolbar and the layers panel is reorderable.
        Assert.Contains("id=\"distributeH\"", html);
        Assert.Contains("id=\"tidyBtn\"", html);
        Assert.Contains("row.draggable = true", html);
        Assert.Contains("function beginRename(", html);
        Assert.Contains("id=\"selectedLocked\"", html);
        // Alt shortcuts mirror the toolbar.
        Assert.Contains("alignWithinSelection('hcenter')", html);
        Assert.Contains("distribute('h')", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-layer.drop-target", css);
        Assert.Contains(".ped-layer .ly-rename", css);
    }

    [Fact]
    public async Task Saving_persists_element_lock_and_opacity()
    {
        // The lock flag and opacity are part of the element contract: they must survive a save and
        // come back through the editor's serialized payload, and the public page must honour them.
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة قفل وشفافية");

        var elementsJson =
            "[{\"elementType\":\"text\",\"content\":\"عنصر مقفل\",\"x\":40,\"y\":60,\"width\":300,\"height\":50," +
            "\"fontSize\":24,\"fontFamily\":\"Tajawal\",\"fontColor\":\"#172033\",\"fontWeight\":\"600\"," +
            "\"textAlign\":\"center\",\"isVisible\":true,\"isLocked\":true,\"zIndex\":1,\"rotation\":0,\"opacity\":42}]";

        var response = await admin.PostFormAsync("/CertificateDesigns", "/CertificateDesigns/Save", new Dictionary<string, string>
        {
            ["CertificateDesignId"] = designId.ToString(),
            ["Name"] = "مسودة قفل وشفافية",
            ["CanvasWidth"] = "1120",
            ["CanvasHeight"] = "792",
            ["BackgroundColor"] = "#fffdf7",
            ["BorderColor"] = "#c9a227",
            ["BorderWidth"] = "12",
            ["BorderRadius"] = "8",
            ["ElementsJson"] = elementsJson,
            ["Publish"] = "false"
        });
        response.EnsureSuccessStatusCode();

        var saved = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesignElements.Single(e => e.CertificateDesignId == designId)));
        Assert.True(saved.IsLocked);
        Assert.Equal(42, saved.Opacity);

        var html = await admin.GetStringAsync($"/CertificateDesigns?id={designId}");
        Assert.Contains("\"isLocked\":true", html);
        Assert.Contains("\"opacity\":42", html);
    }

    [Fact]
    public async Task Editor_exposes_speed_and_access_affordances()
    {
        // Phase 4: clipboard, inline text editing, a right-click menu, a command palette, a
        // shortcuts sheet and rulers — the tools that make a designer fast rather than merely pretty.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        // Clipboard.
        Assert.Contains("function copySelected(", html);
        Assert.Contains("function cutSelected(", html);
        Assert.Contains("function pasteClipboard(", html);
        // Inline text editing.
        Assert.Contains("function beginInlineEdit(", html);
        Assert.Contains("id=\"inlineEdit\"", html);
        // Context menu + command palette + shortcuts sheet.
        Assert.Contains("id=\"ctxMenu\"", html);
        Assert.Contains("function showContextMenu(", html);
        Assert.Contains("id=\"cmdk\"", html);
        Assert.Contains("const COMMANDS = [", html);
        Assert.Contains("function openCommandPalette(", html);
        Assert.Contains("id=\"shortcutsSheet\"", html);
        // Rulers.
        Assert.Contains("id=\"rulerH\"", html);
        Assert.Contains("id=\"rulerV\"", html);
        Assert.Contains("function renderRulers(", html);
        // The clipboard and lock keys must be bound.
        Assert.Contains("e.key.toLowerCase() === 'c'", html);
        Assert.Contains("e.key.toLowerCase() === 'v'", html);
        Assert.Contains("e.key.toLowerCase() === 'l'", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-ruler", css);
        Assert.Contains(".ped-menu-item", css);
        Assert.Contains(".ped-cmdk-item", css);
        Assert.Contains(".ped-sheet-row", css);
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
