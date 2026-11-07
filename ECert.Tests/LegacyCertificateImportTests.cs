using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// End-to-end tests for the legacy (old Excel system) certificate import. They drive the real
/// HTTP surface — upload, preview, inline row edit, per-course configuration, commit, report — and
/// assert the database side effects and the public verification path.
/// </summary>
[Collection(AppCollection.Name)]
public class LegacyCertificateImportTests
{
    private readonly AppFixture _fixture;

    public LegacyCertificateImportTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Preview_groups_rows_by_course_and_writes_nothing()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();

        var workbook = BuildWorkbook(
            ("محمد أحمد", "Mohammed Ahmed", "LEG-5001", "دورة الإعسافات", "2021-03-10"),
            ("سارة عبدالله", "Sara Abdullah", "LEG-5002", "دورة الإعسافات", "2021-04-01"),
            ("", "", "LEG-5003", "دورة الحاسوب", "2022-01-15"),          // invalid: missing arabic name
            ("نورة سالم", "Noura Salem", "LEG-5001", "دورة الحاسوب", "2022-03-01"), // duplicate in file
            ("ماهر سعيد", "Maher Saeed", "LEG-5004", "دورة الحاسوب", "2022-04-01"));

        var response = await UploadAsync(admin, categoryId, instructorId, workbook);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("legacyPreviewTable", html);
        Assert.Contains("courseTable", html);
        Assert.Contains("id=\"confirmCount\">3<", html);      // three valid rows

        // Two distinct courses are detected and grouped.
        Assert.Equal(2, Regex.Matches(html, "data-course-key=").Count);

        // Nothing is written during preview.
        Assert.Null(await _fixture.Factory.FindCertificateAsync("LEG-5001"));
        Assert.Null(await _fixture.Factory.FindCertificateAsync("LEG-5002"));
        Assert.Null(await _fixture.Factory.FindCourseByNameAsync("دورة الإعسافات"));
    }

    [Fact]
    public async Task Commit_creates_one_course_per_distinct_name_with_its_own_category_and_instructors()
    {
        var admin = await SignInAdminAsync();
        var instructors = await _fixture.Factory.InstructorsAsync();
        var healthCategory = await _fixture.Factory.CategoryIdByNameAsync("الرعاية الصحية");
        var itCategory = await _fixture.Factory.CategoryIdByNameAsync("تقنية المعلومات");

        var workbook = BuildWorkbook(
            ("علي حسن", "Ali Hassan", "LEG-6001", "دورة الشبكات", "2020-05-05"),
            ("هدى مراد", "Huda Murad", "LEG-6002", "دورة الشبكات", "2020-06-06"),
            ("بدر علي", "Badr Ali", "LEG-6003", "دورة الأمن", "2020-07-07"));

        var previewHtml = await (await UploadAsync(admin, healthCategory, instructors[0].Id, workbook)).Content.ReadAsStringAsync();
        var token = ExtractToken(previewHtml);

        // Configure the first course: health category, two instructors (shared course).
        var editCourse = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/EditCourse", new[]
        {
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("courseKey", CourseKey("دورة الشبكات")),
            new KeyValuePair<string, string>("categoryId", healthCategory.ToString()),
            new KeyValuePair<string, string>("action", "create"),
            new KeyValuePair<string, string>("instructorIds", instructors[0].Id.ToString()),
            new KeyValuePair<string, string>("instructorIds", instructors[1].Id.ToString())
        });
        editCourse.EnsureSuccessStatusCode();
        Assert.Contains("\"isConfigured\":true", await editCourse.Content.ReadAsStringAsync());

        // Configure the second course with a different category and a single instructor.
        var editOther = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/EditCourse", new[]
        {
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("courseKey", CourseKey("دورة الأمن")),
            new KeyValuePair<string, string>("categoryId", itCategory.ToString()),
            new KeyValuePair<string, string>("action", "create"),
            new KeyValuePair<string, string>("instructorIds", instructors[2].Id.ToString())
        });
        editOther.EnsureSuccessStatusCode();

        var confirm = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/ConfirmImport",
            new[] { new KeyValuePair<string, string>("token", token) });
        confirm.EnsureSuccessStatusCode();
        Assert.Contains("تم الاستيراد", await confirm.Content.ReadAsStringAsync());

        // Each distinct course name became exactly one course, with its own category and instructors.
        var networks = await _fixture.Factory.FindCourseByNameAsync("دورة الشبكات");
        Assert.NotNull(networks);
        Assert.Equal(healthCategory, networks!.CategoryId);
        Assert.Equal(
            new[] { instructors[0].Id, instructors[1].Id }.OrderBy(id => id),
            (await _fixture.Factory.CourseInstructorIdsAsync(networks.CourseId)).OrderBy(id => id));

        var security = await _fixture.Factory.FindCourseByNameAsync("دورة الأمن");
        Assert.NotNull(security);
        Assert.Equal(itCategory, security!.CategoryId);
        Assert.Equal(new[] { instructors[2].Id }, await _fixture.Factory.CourseInstructorIdsAsync(security.CourseId));

        // Certificate carries the course name and the legacy source; no prefix.
        var certificate = await _fixture.Factory.FindCertificateAsync("LEG-6001");
        Assert.NotNull(certificate);
        Assert.Equal("Legacy", certificate!.Source);
        Assert.Null(certificate.CertificatePrefix);
        Assert.Equal("دورة الشبكات", certificate.CourseNameArabic);
    }

    [Fact]
    public async Task Commit_links_an_existing_course_without_modifying_it()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();

        // The seeded course "التمريض المنزلي" already exists; the file names the same course.
        var existing = await _fixture.Factory.FindCourseByNameAsync("التمريض المنزلي");
        Assert.NotNull(existing);
        var coursesBefore = await _fixture.Factory.CountCoursesAsync();

        var workbook = BuildWorkbook(("سالم ناصر", "Salem Nasser", "LEG-6500", "التمريض المنزلي", "2019-02-02"));
        var previewHtml = await (await UploadAsync(admin, categoryId, instructorId, workbook)).Content.ReadAsStringAsync();
        var token = ExtractToken(previewHtml);

        // The preview suggests linking (the matching course is offered).
        Assert.Contains("التمريض المنزلي", System.Net.WebUtility.HtmlDecode(previewHtml));

        var editCourse = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/EditCourse", new[]
        {
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("courseKey", CourseKey("التمريض المنزلي")),
            new KeyValuePair<string, string>("categoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("action", "link"),
            new KeyValuePair<string, string>("linkedCourseId", existing!.CourseId.ToString()),
            new KeyValuePair<string, string>("instructorIds", instructorId.ToString())
        });
        editCourse.EnsureSuccessStatusCode();

        var confirm = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/ConfirmImport",
            new[] { new KeyValuePair<string, string>("token", token) });
        confirm.EnsureSuccessStatusCode();

        // No new course was created and the existing one is unchanged.
        Assert.Equal(coursesBefore, await _fixture.Factory.CountCoursesAsync());
        var after = await _fixture.Factory.FindCourseByNameAsync("التمريض المنزلي");
        Assert.Equal(existing.CategoryId, after!.CategoryId);
        Assert.Equal(existing.InstructorId, after.InstructorId);

        // The certificate is attached to the existing course.
        var certificate = await _fixture.Factory.FindCertificateAsync("LEG-6500");
        Assert.NotNull(certificate);
        var registrationCourseId = await _fixture.Factory.WithDbAsync(db =>
            db.Registrations.Where(r => r.RegistrationId == certificate!.RegistrationId).Select(r => r.CourseId).FirstAsync());
        Assert.Equal(existing.CourseId, registrationCourseId);
    }

    [Fact]
    public async Task Commit_is_blocked_until_every_course_is_configured()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();

        // No defaults given, so the detected course starts unconfigured.
        var workbook = BuildWorkbook(("ليلى محمد", "Layla Mohammed", "LEG-6600", "دورة جديدة تماماً", "2018-08-08"));
        var previewHtml = await (await UploadAsync(admin, null, null, workbook)).Content.ReadAsStringAsync();
        var token = ExtractToken(previewHtml);

        Assert.Contains("id=\"confirmImportBtn\" disabled", previewHtml);

        // Configuring the course unlocks the commit.
        var editCourse = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/EditCourse", new[]
        {
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("courseKey", CourseKey("دورة جديدة تماماً")),
            new KeyValuePair<string, string>("categoryId", categoryId.ToString()),
            new KeyValuePair<string, string>("action", "create"),
            new KeyValuePair<string, string>("instructorIds", instructorId.ToString())
        });
        editCourse.EnsureSuccessStatusCode();
        var json = await editCourse.Content.ReadAsStringAsync();
        Assert.Contains("\"isConfigured\":true", json);
        Assert.Contains("\"canCommit\":true", json);
    }

    [Fact]
    public async Task Formula_errors_are_reported_as_readable_messages_not_raw_errors()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();

        // A workbook whose certificate-number cell holds a broken formula with a cached #REF!.
        var workbook = BuildWorkbookWithFormulaError("متدرب", "Trainee", "دورة معادلات", "2020-01-01");
        var response = await UploadAsync(admin, categoryId, instructorId, workbook);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // The row is flagged and the message explains the Excel error.
        Assert.Contains("#REF!", html);
        Assert.Contains("معادلة", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains("id=\"confirmCount\">0<", html);
    }

    [Fact]
    public async Task Imported_certificate_verifies_by_number_and_by_code()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();

        var workbook = BuildWorkbook(("ريم فهد", "Reem Fahd", "LEG-7001", "دورة التصميم", "2019-09-09"));
        var previewHtml = await (await UploadAsync(admin, categoryId, instructorId, workbook)).Content.ReadAsStringAsync();
        var token = ExtractToken(previewHtml);

        await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/ConfirmImport",
            new[] { new KeyValuePair<string, string>("token", token) });

        var certificate = await _fixture.Factory.FindCertificateAsync("LEG-7001");
        Assert.NotNull(certificate);

        // The verification form is public and needs no admin session.
        var anonymous = AppClient.Create(_fixture.Factory);
        var byNumber = await anonymous.PostFormAsync("/Certificates/Verify", "/Certificates/Verify",
            new[] { new KeyValuePair<string, string>("code", "LEG-7001") });
        byNumber.EnsureSuccessStatusCode();
        Assert.Contains("LEG-7001", await byNumber.Content.ReadAsStringAsync());

        var byCode = await anonymous.PostFormAsync("/Certificates/Verify", "/Certificates/Verify",
            new[] { new KeyValuePair<string, string>("code", certificate!.VerificationCode) });
        byCode.EnsureSuccessStatusCode();
        Assert.Contains("LEG-7001", await byCode.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Imported_certificate_shows_a_barcode_when_the_design_has_one()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();

        var workbook = BuildWorkbook(("ليان ناصر", "Layan Nasser", "LEG-9001", "دورة الجودة", "2020-05-05"));
        var previewHtml = await (await UploadAsync(admin, categoryId, instructorId, workbook)).Content.ReadAsStringAsync();
        var token = ExtractToken(previewHtml);

        await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/ConfirmImport",
            new[] { new KeyValuePair<string, string>("token", token) });

        await _fixture.Factory.WithDbAsync(async db =>
        {
            // Retire any published design, then publish one that renders a barcode of the certificate number.
            foreach (var existing in db.CertificateDesigns.Where(d => d.IsPublished))
                existing.IsPublished = false;

            var design = CertificateDesignService.CreateDefault("test");
            design.DesignKey = "test-barcode-" + Guid.NewGuid().ToString("N");
            design.Elements.Add(new ECert.Models.CertificateDesignElement
            {
                ElementType = "barcode",
                FieldKey = "certificate_number",
                X = 100, Y = 640, Width = 320, Height = 70,
                FontSize = 14, FontFamily = "Tajawal", FontColor = "#172033",
                FontWeight = "600", TextAlign = "center", IsVisible = true, SortOrder = 99, ZIndex = 99
            });
            db.CertificateDesigns.Add(design);
            await db.SaveChangesAsync();
            return true;
        });

        var anonymous = AppClient.Create(_fixture.Factory);
        var verify = await anonymous.PostFormAsync("/Certificates/Verify", "/Certificates/Verify",
            new[] { new KeyValuePair<string, string>("code", "LEG-9001") });
        verify.EnsureSuccessStatusCode();
        var html = System.Net.WebUtility.HtmlDecode(await verify.Content.ReadAsStringAsync());

        Assert.Contains("باركود الشهادة", html);
        Assert.Contains("data:image/svg+xml;base64,", html);
    }

    [Fact]
    public async Task Editing_a_row_revalidates_it_and_unlocks_commit()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();

        // The only row is invalid (missing the Arabic name).
        var workbook = BuildWorkbook(("", "Solo English", "LEG-8001", "دورة المهارات", "2018-01-01"));
        var previewHtml = await (await UploadAsync(admin, categoryId, instructorId, workbook)).Content.ReadAsStringAsync();
        var token = ExtractToken(previewHtml);

        var edit = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/EditRow", new[]
        {
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("rowNumber", "2"),
            new KeyValuePair<string, string>("fullNameArabic", "اسم مصحح"),
            new KeyValuePair<string, string>("fullNameEnglish", "Solo English"),
            new KeyValuePair<string, string>("certificateNumber", "LEG-8001"),
            new KeyValuePair<string, string>("courseKey", CourseKey("دورة المهارات")),
            new KeyValuePair<string, string>("issueDate", "2018-01-01")
        });
        edit.EnsureSuccessStatusCode();
        var json = await edit.Content.ReadAsStringAsync();

        Assert.Contains("\"isValid\":true", json);
        Assert.Contains("\"canCommit\":true", json);

        var confirm = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/ConfirmImport",
            new[] { new KeyValuePair<string, string>("token", token) });
        confirm.EnsureSuccessStatusCode();

        var imported = await _fixture.Factory.FindCertificateAsync("LEG-8001");
        Assert.NotNull(imported);
        Assert.Equal("اسم مصحح", imported!.TraineeNameArabic);
    }

    [Fact]
    public async Task A_user_without_the_issue_permission_is_denied()
    {
        var anonymous = AppClient.Create(_fixture.Factory);
        Assert.False(await anonymous.CanOpenAsync("/CertificatesImport/Import"));
    }

    [Fact]
    public async Task Import_without_a_file_shows_validation_not_500()
    {
        var admin = await SignInAdminAsync();

        var response = await admin.PostFormAsync("/CertificatesImport/Import", "/CertificatesImport/Import",
            new[] { new KeyValuePair<string, string>("DefaultCategoryId", "1") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("a-note--danger", await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        AppClient admin, int? categoryId, int? instructorId, byte[] workbook)
    {
        var fields = new List<KeyValuePair<string, string>>();
        if (categoryId is int category) fields.Add(new KeyValuePair<string, string>("DefaultCategoryId", category.ToString()));
        if (instructorId is int instructor) fields.Add(new KeyValuePair<string, string>("DefaultInstructorId", instructor.ToString()));

        return await admin.PostMultipartAsync("/CertificatesImport/Import", "/CertificatesImport/Import",
            fields, "File", "legacy.xlsx", workbook);
    }

    private static string ExtractToken(string html)
    {
        var match = Regex.Match(html, "\"token\":\"([0-9a-f]{32})\"");
        Assert.True(match.Success, "preview token not found in the page");
        return match.Groups[1].Value;
    }

    private static string CourseKey(string courseName)
        => string.Join(' ', courseName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>Builds an XLSX whose columns mirror the legacy file layout.</summary>
    private static byte[] BuildWorkbook(params (string Arabic, string English, string Number, string Course, string IssueDate)[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("الشهادات");
        sheet.Cell(1, 1).Value = "الاسم بالعربية";
        sheet.Cell(1, 2).Value = "الاسم بالإنجليزية";
        sheet.Cell(1, 3).Value = "رقم الشهادة";
        sheet.Cell(1, 4).Value = "اسم الدورة";
        sheet.Cell(1, 5).Value = "تاريخ الإصدار";

        for (var i = 0; i < rows.Length; i++)
        {
            var row = i + 2;
            sheet.Cell(row, 1).Value = rows[i].Arabic;
            sheet.Cell(row, 2).Value = rows[i].English;
            sheet.Cell(row, 3).Value = rows[i].Number;
            sheet.Cell(row, 4).Value = rows[i].Course;
            sheet.Cell(row, 5).Value = rows[i].IssueDate;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Builds a workbook whose certificate-number cell is a formula carrying a cached <c>#REF!</c>
    /// error, the way a file with a broken reference is stored on disk.
    /// </summary>
    private static byte[] BuildWorkbookWithFormulaError(string arabic, string english, string course, string issueDate)
    {
        const string sheetXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <sheetData>
            <row r="1">
            <c r="A1" t="inlineStr"><is><t>الاسم بالعربية</t></is></c>
            <c r="B1" t="inlineStr"><is><t>الاسم بالإنجليزية</t></is></c>
            <c r="C1" t="inlineStr"><is><t>رقم الشهادة</t></is></c>
            <c r="D1" t="inlineStr"><is><t>اسم الدورة</t></is></c>
            <c r="E1" t="inlineStr"><is><t>تاريخ الإصدار</t></is></c>
            </row>
            <row r="2">
            <c r="A2" t="inlineStr"><is><t>__ARABIC__</t></is></c>
            <c r="B2" t="inlineStr"><is><t>__ENGLISH__</t></is></c>
            <c r="C2" t="e"><f>#REF!+1</f><v>#REF!</v></c>
            <c r="D2" t="inlineStr"><is><t>__COURSE__</t></is></c>
            <c r="E2" t="inlineStr"><is><t>__ISSUE__</t></is></c>
            </row>
            </sheetData>
            </worksheet>
            """;

        var sheet = sheetXml
            .Replace("__ARABIC__", arabic)
            .Replace("__ENGLISH__", english)
            .Replace("__COURSE__", course)
            .Replace("__ISSUE__", issueDate);

        const string contentTypes = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """;

        const string rootRels = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """;

        const string workbookXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
            <sheets><sheet name="s" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """;

        const string workbookRels = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """;

        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", contentTypes);
            WriteEntry(zip, "_rels/.rels", rootRels);
            WriteEntry(zip, "xl/workbook.xml", workbookXml);
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", workbookRels);
            WriteEntry(zip, "xl/worksheets/sheet1.xml", sheet);
        }

        return stream.ToArray();
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, System.IO.Compression.CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }
}
