using System.Net;
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
    public async Task Delete_refuses_the_last_published_design()
    {
        // At least one published template must survive as the fallback for courses without their
        // own design, so the only live template cannot be removed.
        var admin = await SignInAdminAsync();
        var publishedId = await CreateDesignAsync("القالب المنشور الوحيد", published: true);
        await MakeOnlyPublishedAsync(publishedId);

        var response = await admin.PostFormAsync("/CertificateDesigns", $"/CertificateDesigns/Delete/{publishedId}", new Dictionary<string, string>());
        response.EnsureSuccessStatusCode();

        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Any(d => d.CertificateDesignId == publishedId))),
            "the only published design must survive a delete request");
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
    public async Task Every_template_row_exposes_a_delete_control()
    {
        // The delete control is now offered per row (not only for the selected draft); the server
        // still refuses to remove the last published template or one a course points at.
        var admin = await SignInAdminAsync();
        var draftId = await CreateDesignAsync("مسودة لها زر حذف");

        var html = await admin.GetStringAsync($"/CertificateDesigns?id={draftId}");
        Assert.Contains($"/CertificateDesigns/Delete/{draftId}", html);
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
    public async Task Saving_persists_typography_detail_and_grouping()
    {
        // Phase 5 fields are additive: italic/underline, line height, letter spacing and the group
        // token must round-trip through save and reappear in the editor payload, and the group id
        // must be sanitised (only letters, digits, dash and underscore survive).
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة أنماط وتجميع");

        var elementsJson =
            "[{\"elementType\":\"text\",\"content\":\"عنوان\",\"x\":40,\"y\":60,\"width\":300,\"height\":50," +
            "\"fontSize\":24,\"fontFamily\":\"Tajawal\",\"fontColor\":\"#172033\",\"fontWeight\":\"700\"," +
            "\"textAlign\":\"center\",\"isItalic\":true,\"isUnderline\":true,\"lineHeight\":160," +
            "\"letterSpacing\":3,\"groupId\":\"g-abc 123!\",\"isVisible\":true,\"isLocked\":false," +
            "\"zIndex\":1,\"rotation\":0,\"opacity\":100}]";

        var response = await admin.PostFormAsync("/CertificateDesigns", "/CertificateDesigns/Save", new Dictionary<string, string>
        {
            ["CertificateDesignId"] = designId.ToString(),
            ["Name"] = "مسودة أنماط وتجميع",
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
        Assert.True(saved.IsItalic);
        Assert.True(saved.IsUnderline);
        Assert.Equal(160, saved.LineHeight);
        Assert.Equal(3, saved.LetterSpacing);
        Assert.Equal("g-abc123", saved.GroupId);

        var html = await admin.GetStringAsync($"/CertificateDesigns?id={designId}");
        Assert.Contains("\"isItalic\":true", html);
        Assert.Contains("\"lineHeight\":160", html);
        Assert.Contains("\"letterSpacing\":3", html);
        Assert.Contains("\"groupId\":\"g-abc123\"", html);
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

    // Phase 5 — the pro-polish pass: grouping, typography detail, style presets, drag-to-place and
    // the print safe area. These are contract checks against the rendered designer and its assets.
    [Fact]
    public async Task Editor_exposes_pro_polish_affordances()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        // Grouping: shared group token plus the two toolbar actions.
        Assert.Contains("id=\"groupBtn\"", html);
        Assert.Contains("id=\"ungroupBtn\"", html);
        Assert.Contains("function groupSelected(", html);
        Assert.Contains("function ungroupSelected(", html);
        Assert.Contains("function expandSelectionToGroups(", html);
        // Typography detail: italic, underline, line height and letter spacing.
        Assert.Contains("id=\"italicBtn\"", html);
        Assert.Contains("id=\"underlineBtn\"", html);
        Assert.Contains("id=\"selectedLineHeight\"", html);
        Assert.Contains("id=\"selectedLetterSpacing\"", html);
        Assert.Contains("isItalic", html);
        Assert.Contains("isUnderline", html);
        Assert.Contains("lineHeight", html);
        Assert.Contains("letterSpacing", html);
        // Style presets.
        Assert.Contains("id=\"presetGrid\"", html);
        Assert.Contains("const STYLE_PRESETS", html);
        Assert.Contains("function applyPreset(", html);
        // Drag from the library onto the canvas.
        Assert.Contains("application/x-ecert-element", html);
        Assert.Contains("function makeLibraryDraggable(", html);
        // Print safe area.
        Assert.Contains("id=\"safeAreaToggle\"", html);
        Assert.Contains("id=\"safeArea\"", html);
        // Keyboard bindings for the new actions.
        Assert.Contains("e.key.toLowerCase() === 'g'", html);
        Assert.Contains("e.key.toLowerCase() === 'i'", html);
        Assert.Contains("e.key.toLowerCase() === 'u'", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-preset", css);
        Assert.Contains(".ped-safe-area", css);
        Assert.Contains(".ly-group", css);
    }

    // The usability pass: drag responsiveness, arbitrary image upload, and a fullscreen workspace.
    [Fact]
    public async Task Editor_moves_the_element_node_during_a_drag()
    {
        // Regression: a single-element drag only moved the selection overlay, so the artwork itself
        // stayed put and the move appeared to fail. The move loop must reposition the element node.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("function moveNode(", html);
        // The drag paints through the element node, and the render loop is frame-throttled.
        Assert.Contains("moveNode(el, group[0]);", html);
        Assert.Contains("requestAnimationFrame(paint)", html);
        // Reading the canvas rect per pointermove forced a layout on every frame; the drag uses a
        // cached scale instead.
        Assert.Contains("function cachedScale(", html);
        Assert.Contains("const s = () => cachedScale();", html);
        // A cancelled pointer must end the drag rather than stranding the element under the cursor.
        Assert.Contains("'pointercancel'", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-app.is-dragging", css);
        Assert.Contains("touch-action: none", css);
    }

    [Fact]
    public async Task Editor_can_add_a_static_image_element()
    {
        // Any bitmap the admin uploads — not just the dynamic partner logos — must be placeable.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("id=\"addImageBtn\"", html);
        Assert.Contains("id=\"imageUploadInput\"", html);
        Assert.Contains("function pickStaticImage(", html);
        Assert.Contains("el-image", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-el.el-image", css);
    }

    [Fact]
    public async Task Saving_persists_a_static_image_element()
    {
        // The image element stores an uploaded path in Content; it must round-trip through save and
        // come back into the editor payload so the bitmap is drawn again on reload.
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة صورة ثابتة");

        var elementsJson =
            "[{\"elementType\":\"image\",\"content\":\"/uploads/designs/abc123.png\",\"x\":40,\"y\":60," +
            "\"width\":220,\"height\":140,\"fontSize\":24,\"fontFamily\":\"Tajawal\",\"fontColor\":\"#172033\"," +
            "\"fontWeight\":\"600\",\"textAlign\":\"center\",\"isVisible\":true,\"isLocked\":false," +
            "\"zIndex\":1,\"rotation\":0,\"opacity\":100}]";

        var response = await admin.PostFormAsync("/CertificateDesigns", "/CertificateDesigns/Save", new Dictionary<string, string>
        {
            ["CertificateDesignId"] = designId.ToString(),
            ["Name"] = "مسودة صورة ثابتة",
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
        Assert.Equal("image", saved.ElementType);
        Assert.Equal("/uploads/designs/abc123.png", saved.Content);
    }

    [Fact]
    public async Task Saving_rejects_a_static_image_with_a_foreign_path()
    {
        // Content is re-validated server-side: a path outside our own upload directory must be
        // refused rather than stored and later rendered.
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة مسار صورة غير صالح");

        var elementsJson =
            "[{\"elementType\":\"image\",\"content\":\"https://evil.example/x.png\",\"x\":40,\"y\":60," +
            "\"width\":220,\"height\":140,\"isVisible\":true,\"zIndex\":1,\"rotation\":0,\"opacity\":100}]";

        var response = await admin.PostFormAsync("/CertificateDesigns", "/CertificateDesigns/Save", new Dictionary<string, string>
        {
            ["CertificateDesignId"] = designId.ToString(),
            ["Name"] = "مسودة مسار صورة غير صالح",
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

        var stored = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesignElements.Count(e => e.CertificateDesignId == designId)));
        Assert.Equal(0, stored);
    }

    [Fact]
    public async Task Upload_endpoint_stores_a_valid_image_and_rejects_a_fake_one()
    {
        var admin = await SignInAdminAsync();

        // A minimal PNG (magic bytes + a single IHDR chunk header) passes the signature check.
        var png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01
        };
        var ok = await admin.PostMultipartAsync(
            "/CertificateDesigns", "/CertificateDesigns/UploadImage",
            Array.Empty<KeyValuePair<string, string>>(), "file", "logo.png", png, "image/png");
        ok.EnsureSuccessStatusCode();
        var body = await ok.Content.ReadAsStringAsync();
        Assert.Contains("/uploads/designs/", body);

        // A file whose extension claims PNG but whose bytes do not is refused.
        var fake = await admin.PostMultipartAsync(
            "/CertificateDesigns", "/CertificateDesigns/UploadImage",
            Array.Empty<KeyValuePair<string, string>>(), "file", "evil.png",
            System.Text.Encoding.ASCII.GetBytes("<html></html>"), "image/png");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, fake.StatusCode);
    }

    [Fact]
    public async Task Editor_exposes_a_fullscreen_workspace()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateDesigns");

        Assert.Contains("id=\"fullscreenBtn\"", html);
        Assert.Contains("function setFullscreen(", html);
        Assert.Contains("ped-fullscreen", html);
        // F toggles it, Escape leaves it.
        Assert.Contains("setFullscreen(!document.getElementById('pedApp').classList.contains('ped-fullscreen'))", html);

        var css = await admin.GetStringAsync("/css/certificate-design-editor.css");
        Assert.Contains(".ped-app.ped-fullscreen", css);
        Assert.Contains("position: fixed", css);
    }

    private async Task<int> CreateDesignAsync(string name, bool published = false)
    {
        var id = await _fixture.Factory.WithDbAsync(async db =>
        {
            var design = new CertificateDesign
            {
                Name = name,
                DesignKey = $"draft-{Guid.NewGuid():N}",
                IsPublished = published,
                UpdatedBy = "test"
            };
            db.CertificateDesigns.Add(design);
            await db.SaveChangesAsync();
            return design.CertificateDesignId;
        });
        return id;
    }

    /// <summary>
    /// Leaves exactly <paramref name="keepId"/> published so a test's published-set is deterministic
    /// regardless of what other tests in the shared fixture database created.
    /// </summary>
    private Task MakeOnlyPublishedAsync(int keepId) =>
        _fixture.Factory.ExecuteRawAsync(
            $"UPDATE CertificateDesigns SET IsPublished = 0 WHERE CertificateDesignId <> {keepId};");

    // ---------------------------------------------------------------------------------------------
    // Template management: several templates may be published at once, a template can be renamed in
    // place (including the published one), duplicated, and deleted — while at least one published
    // template always survives as the fallback.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_a_template_does_not_unpublish_the_others()
    {
        // Regression: publishing a template used to flip every other published template to draft, so
        // only one template could ever be live and the rest silently became dead drafts.
        var admin = await SignInAdminAsync();
        var existingPublishedId = await CreateDesignAsync("قالب منشور مسبقًا", published: true);
        await MakeOnlyPublishedAsync(existingPublishedId);

        var response = await admin.PostFormAsync("/CertificateDesigns", "/CertificateDesigns/Save", new Dictionary<string, string>
        {
            ["CertificateDesignId"] = "0",
            ["Name"] = $"قالب ثانٍ {Guid.NewGuid():N}"[..24],
            ["CanvasWidth"] = "1120",
            ["CanvasHeight"] = "792",
            ["BackgroundColor"] = "#fffdf7",
            ["BorderColor"] = "#c9a227",
            ["BorderWidth"] = "12",
            ["BorderRadius"] = "8",
            ["ElementsJson"] = "[]",
            ["Publish"] = "true"
        });
        response.EnsureSuccessStatusCode();

        var published = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Count(d => d.IsPublished)));
        Assert.Equal(2, published);
        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Single(d => d.CertificateDesignId == existingPublishedId).IsPublished)),
            "publishing a new template must leave the existing published template live");
    }

    [Fact]
    public async Task Renaming_the_published_template_renames_it_in_place()
    {
        // Regression: saving the published template without publishing forked a new draft named
        // "<name> (مسودة)" and left the original name untouched, so a rename looked like it failed
        // and produced duplicates. Renaming must now change the name of the same row.
        var admin = await SignInAdminAsync();
        var publishedId = await CreateDesignAsync("قالب منشور لإعادة التسمية", published: true);

        var before = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Count()));

        var name = $"اسم جديد {Guid.NewGuid():N}"[..24];
        var response = await admin.PostFormAsync("/CertificateDesigns", $"/CertificateDesigns/Rename/{publishedId}",
            new Dictionary<string, string> { ["name"] = name });
        response.EnsureSuccessStatusCode();

        var stored = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Single(d => d.CertificateDesignId == publishedId).Name));
        Assert.Equal(name, stored);

        var after = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Count()));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Rename_without_an_antiforgery_token_is_rejected()
    {
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("مسودة ومحاولة إعادة تسمية");

        var response = await admin.PostWithoutTokenAsync($"/CertificateDesigns/Rename/{designId}",
            new Dictionary<string, string> { ["name"] = "اسم مزوّر" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Single(d => d.CertificateDesignId == designId).Name != "اسم مزوّر")),
            "a CSRF rename attempt must not change the name");
    }

    [Fact]
    public async Task Duplicate_copies_the_template_and_its_elements_as_a_draft()
    {
        var admin = await SignInAdminAsync();
        var sourceId = await CreateDesignAsync("قالب للنسخ");
        await _fixture.Factory.WithDbAsync(async db =>
        {
            db.CertificateDesignElements.Add(new CertificateDesignElement
            {
                CertificateDesignId = sourceId,
                ElementType = "field",
                FieldKey = "trainee_name"
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await admin.PostFormAsync("/CertificateDesigns", $"/CertificateDesigns/Duplicate/{sourceId}", new Dictionary<string, string>());
        response.EnsureSuccessStatusCode();

        var copy = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns
                .Where(d => d.CertificateDesignId != sourceId && d.Name.StartsWith("قالب للنسخ"))
                .Select(d => new { d.CertificateDesignId, d.IsPublished })
                .FirstOrDefault()));
        Assert.NotNull(copy);
        Assert.False(copy!.IsPublished);
        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesignElements.Any(e => e.CertificateDesignId == copy.CertificateDesignId))),
            "the duplicate must carry the source's elements");
    }

    [Fact]
    public async Task A_published_template_can_be_deleted_when_another_published_one_remains()
    {
        var admin = await SignInAdminAsync();
        var firstId = await CreateDesignAsync("قالب منشور أول", published: true);
        var secondId = await CreateDesignAsync("قالب منشور ثانٍ", published: true);
        await MakeOnlyPublishedAsync(firstId);
        await _fixture.Factory.ExecuteRawAsync($"UPDATE CertificateDesigns SET IsPublished = 1 WHERE CertificateDesignId IN ({firstId}, {secondId});");

        var response = await admin.PostFormAsync("/CertificateDesigns", $"/CertificateDesigns/Delete/{firstId}", new Dictionary<string, string>());
        response.EnsureSuccessStatusCode();

        Assert.False(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Any(d => d.CertificateDesignId == firstId))));
        Assert.True(await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Any(d => d.CertificateDesignId == secondId))));
    }

    [Fact]
    public async Task New_templates_are_suggested_distinct_names()
    {
        // Regression: every new template started with the same name, so the list showed several
        // identical entries. A fresh template now gets a free "قالب جديد N" that no other design uses.
        var admin = await SignInAdminAsync();
        await admin.PostFormAsync("/CertificateDesigns", "/CertificateDesigns/Save", new Dictionary<string, string>
        {
            ["CertificateDesignId"] = "0",
            ["Name"] = "قالب جديد",
            ["CanvasWidth"] = "1120",
            ["CanvasHeight"] = "792",
            ["BackgroundColor"] = "#fffdf7",
            ["BorderColor"] = "#c9a227",
            ["BorderWidth"] = "12",
            ["BorderRadius"] = "8",
            ["ElementsJson"] = "[]",
            ["Publish"] = "false"
        });

        var existing = await _fixture.Factory.WithDbAsync(db =>
            Task.FromResult(db.CertificateDesigns.Select(d => d.Name).ToList()));

        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/CertificateDesigns/New"));
        var match = System.Text.RegularExpressions.Regex.Match(html, "id=\"templateName\"[^>]*value=\"([^\"]*)\"");
        Assert.True(match.Success, "the new-template page must render the name field");
        var suggested = match.Groups[1].Value;

        Assert.StartsWith("قالب جديد", suggested);
        Assert.DoesNotContain(suggested, existing);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }
}
