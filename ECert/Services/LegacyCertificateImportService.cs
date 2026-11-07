using ECert.Data;
using ECert.Models;
using ECert.Services.Excel;
using ECert.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Imports certificates that were issued by the old (Excel-based) system. A file carries the
/// trainee names, the historical certificate number, the course name and the issue date. Because a
/// single file usually mixes several courses — each with its own category and instructors — the
/// preview groups the rows by course name and lets the administrator configure each group (category,
/// one or more instructors, and whether to create a new course or link an existing one) before
/// anything is written.
///
/// The import is deliberately two-phase (preview, then commit) so an administrator can review and
/// correct the parsed rows before anything is written. Certificate numbers are preserved verbatim —
/// that is what lets a former student verify a certificate they already hold.
/// </summary>
public sealed class LegacyCertificateImportService
{
    public const string Source = "Legacy";
    public const string SymbolicPhone = "LEGACY";
    public const string HeardFromLabel = "استيراد شهادات قديمة";

    private readonly ECertDbContext _db;
    private readonly CertificateSecurityService _security;
    private readonly CertificateQrService _qr;
    private readonly AuditLogService _audit;

    public LegacyCertificateImportService(
        ECertDbContext db,
        CertificateSecurityService security,
        CertificateQrService qr,
        AuditLogService audit)
    {
        _db = db;
        _security = security;
        _qr = qr;
        _audit = audit;
    }

    /// <summary>Parses and validates an uploaded workbook without touching the database.</summary>
    public async Task<LegacyCertificateImportPreview> BuildPreviewAsync(
        Stream stream,
        int? defaultInstructorId,
        int? defaultCategoryId)
    {
        using var import = ExcelImport.Open(stream, ExcelColumns.LegacyCertificateAliases);

        var missing = import.MissingColumns([ExcelColumns.FullNameArabic, ExcelColumns.CertificateNumber, ExcelColumns.CourseName]);
        if (missing.Count > 0)
            throw new ExcelImportException("الأعمدة المطلوبة غير موجودة في الملف: الاسم بالعربية، رقم الشهادة، اسم الدورة.");

        var preview = new LegacyCertificateImportPreview { HasFormulas = import.HasFormulas };

        var groups = new Dictionary<string, LegacyImportCourseGroup>(StringComparer.OrdinalIgnoreCase);
        var numbersSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var row = ExcelImport.FirstDataRow; row <= import.LastRow; row++)
        {
            var (arabic, arabicIssue) = import.Cell(row, ExcelColumns.FullNameArabic, "الاسم بالعربية");
            var (english, englishIssue) = import.Cell(row, ExcelColumns.FullNameEnglish, "الاسم بالإنجليزية");
            var (numberRaw, numberIssue) = import.Cell(row, ExcelColumns.CertificateNumber, "رقم الشهادة");
            var (courseRaw, courseIssue) = import.Cell(row, ExcelColumns.CourseName, "اسم الدورة");

            var arabicName = arabic.Trim();
            var englishName = english.Trim();
            var number = CollapseSpaces(numberRaw);
            var course = CollapseSpaces(courseRaw);

            // Skip blank filler rows, but never swallow a row that failed to read.
            if (arabicName.Length == 0 && englishName.Length == 0 && number.Length == 0 && course.Length == 0
                && !arabicIssue.HasIssue && !englishIssue.HasIssue && !numberIssue.HasIssue && !courseIssue.HasIssue)
                continue;

            var errors = new List<string>();
            if (arabicIssue.HasIssue) errors.Add(arabicIssue.Message("الاسم بالعربية"));
            else if (arabicName.Length == 0) errors.Add("الاسم بالعربية مطلوب.");

            if (englishIssue.HasIssue) errors.Add(englishIssue.Message("الاسم بالإنجليزية"));

            if (numberIssue.HasIssue) errors.Add(numberIssue.Message("رقم الشهادة"));
            else if (number.Length == 0) errors.Add("رقم الشهادة مطلوب.");

            if (courseIssue.HasIssue) errors.Add(courseIssue.Message("اسم الدورة"));
            else if (course.Length == 0) errors.Add("اسم الدورة مطلوب.");

            var issueDate = import.Date(row, ExcelColumns.IssueDate, "تاريخ الإصدار", errors);

            var entry = new LegacyCertificateImportRow
            {
                RowNumber = row,
                FullNameArabic = arabicName,
                FullNameEnglish = englishName,
                CertificateNumber = number,
                CourseName = course,
                IssueDate = issueDate,
                Errors = errors
            };
            entry.IsValid = errors.Count == 0;

            // Duplicate numbers inside the same file: keep the first, flag the rest.
            if (entry.IsValid)
            {
                if (numbersSeen.TryGetValue(number, out var firstRow))
                {
                    entry.IsValid = false;
                    entry.IsDuplicate = true;
                    entry.Errors.Add($"رقم الشهادة مكرر داخل الملف (الصف {firstRow}).");
                }
                else
                {
                    numbersSeen[number] = row;
                }
            }

            if (course.Length > 0)
            {
                entry.CourseKey = CourseKey(course);
                if (!groups.TryGetValue(entry.CourseKey, out var group))
                {
                    group = new LegacyImportCourseGroup
                    {
                        Key = entry.CourseKey,
                        NameArabic = course,
                        NameEnglish = englishName,
                        CategoryId = defaultCategoryId,
                        InstructorIds = defaultInstructorId is int instructorId ? new List<int> { instructorId } : new List<int>()
                    };
                    groups[entry.CourseKey] = group;
                }
                else if (group.NameEnglish.Length == 0 && englishName.Length > 0)
                {
                    group.NameEnglish = englishName;
                }
            }

            preview.Rows.Add(entry);
        }

        preview.TotalRows = preview.Rows.Count;
        if (preview.TotalRows == 0)
            throw new ExcelImportException("لا يوجد صفوف بيانات في الملف.");

        // Duplicate numbers already present in the system are skipped on commit.
        var candidateNumbers = preview.Rows.Where(r => r.IsValid).Select(r => r.CertificateNumber).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidateNumbers.Count > 0)
        {
            var existing = await _db.Certificates
                .AsNoTracking()
                .Where(c => candidateNumbers.Contains(c.CertificateNumber))
                .Select(c => c.CertificateNumber)
                .ToListAsync();
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in preview.Rows.Where(r => r.IsValid && existingSet.Contains(r.CertificateNumber)))
            {
                entry.IsValid = false;
                entry.IsDuplicate = true;
                entry.Errors.Add("رقم الشهادة موجود مسبقاً في النظام — سيتم تجاهله.");
            }
        }

        // Suggest linking an existing course whenever a name matches one already in the system.
        foreach (var group in groups.Values)
        {
            group.MatchingCourses = await FindMatchingCoursesAsync(group.NameArabic);
            if (group.MatchingCourses.Count > 0)
            {
                group.Action = LegacyCourseAction.Link;
                group.LinkedCourseId = group.MatchingCourses[0].Id;
                group.LinkedCourseLabel = group.MatchingCourses[0].Name;
            }
        }

        // Keep every group (a row can still become valid through an inline edit), but only groups
        // that will actually receive certificates block the commit.
        preview.Courses = groups.Values
            .OrderBy(g => g.NameArabic, StringComparer.CurrentCulture)
            .ToList();
        foreach (var group in preview.Courses)
            group.HasValidRows = preview.Rows.Any(r => r.IsValid && string.Equals(r.CourseKey, group.Key, StringComparison.OrdinalIgnoreCase));

        Recalculate(preview);

        return preview;
    }

    /// <summary>Writes the approved rows: creates or links each course, then its registrations and certificates.</summary>
    public async Task<LegacyCertificateImportResult> CommitAsync(
        LegacyCertificateImportPreview preview,
        string actor,
        string publicBaseUrl,
        string? ip)
    {
        var result = new LegacyCertificateImportResult();

        var validRows = preview.Rows.Where(r => r.IsValid).ToList();
        var skippedRows = preview.Rows.Where(r => !r.IsValid).ToList();
        var groupsByKey = preview.Courses.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var now = DateTime.Now;

        // Resolve every course group once: link to the chosen existing course, or create a new one.
        var courseIdsByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in preview.Courses)
        {
            if (group.Action == LegacyCourseAction.Link && group.LinkedCourseId is int linkedId)
            {
                courseIdsByKey[group.Key] = linkedId;
                result.CoursesLinked++;
                continue;
            }

            var course = new Course
            {
                CourseName = group.NameArabic,
                CourseNameArabic = group.NameArabic,
                CourseNameEnglish = string.IsNullOrWhiteSpace(group.NameEnglish) ? group.NameArabic : group.NameEnglish,
                CategoryId = group.CategoryId!.Value,
                InstructorId = group.InstructorIds[0],
                Status = "Completed",
                Price = 0,
                CreatedAt = now
            };
            _db.Courses.Add(course);
            await _db.SaveChangesAsync();

            for (var index = 0; index < group.InstructorIds.Count; index++)
            {
                _db.CourseInstructors.Add(new CourseInstructor
                {
                    CourseId = course.CourseId,
                    InstructorId = group.InstructorIds[index],
                    SortOrder = index
                });
            }

            courseIdsByKey[group.Key] = course.CourseId;
            result.CoursesCreated++;
        }

        var reservedPublicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedVerificationCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in validRows)
        {
            var group = groupsByKey[row.CourseKey];
            var courseId = courseIdsByKey[row.CourseKey];
            var issueDate = row.IssueDate ?? now;
            var courseNameArabic = group.NameArabic;
            var courseNameEnglish = string.IsNullOrWhiteSpace(group.NameEnglish) ? group.NameArabic : group.NameEnglish;

            var registration = new Registration
            {
                RequestNumber = await GenerateRequestNumberAsync(),
                FullName = row.FullNameArabic,
                FullNameArabic = row.FullNameArabic,
                FullNameEnglish = string.IsNullOrWhiteSpace(row.FullNameEnglish) ? row.FullNameArabic : row.FullNameEnglish,
                Phone = SymbolicPhone,
                CourseId = courseId,
                Status = "Accepted",
                RegistrationDate = issueDate,
                AcceptedDate = issueDate,
                ProcessedBy = actor,
                CertificateIssued = true,
                HeardFrom = HeardFromLabel
            };
            _db.Registrations.Add(registration);

            var certificate = new Certificate
            {
                CertificateNumber = row.CertificateNumber,
                PublicId = GenerateUniqueToken(reservedPublicIds, _security.GeneratePublicId),
                VerificationCode = GenerateUniqueToken(reservedVerificationCodes, _security.GenerateVerificationCode),
                Registration = registration,
                TraineeName = row.FullNameArabic,
                TraineeNameArabic = row.FullNameArabic,
                TraineeNameEnglish = string.IsNullOrWhiteSpace(row.FullNameEnglish) ? row.FullNameArabic : row.FullNameEnglish,
                CourseName = courseNameArabic,
                CourseNameArabic = courseNameArabic,
                CourseNameEnglish = courseNameEnglish,
                IssueDate = issueDate,
                IssuedBy = actor,
                Status = "Valid",
                SignatureVersion = 1,
                Source = Source,
                CreatedAt = now
            };
            _db.Certificates.Add(certificate);

            var verificationUrl = _security.BuildVerificationUrl(certificate.PublicId, publicBaseUrl);
            result.Rows.Add(new LegacyCertificateImportResultRow
            {
                CertificateNumber = certificate.CertificateNumber,
                TraineeNameArabic = certificate.TraineeNameArabic ?? certificate.TraineeName,
                TraineeNameEnglish = certificate.TraineeNameEnglish ?? certificate.TraineeName,
                CourseName = courseNameArabic,
                PublicId = certificate.PublicId,
                VerificationCode = certificate.VerificationCode,
                VerificationUrl = verificationUrl,
                QrDataUri = _qr.BuildDataUri(verificationUrl, 4),
                Imported = true
            });

            result.Imported++;
        }

        foreach (var row in skippedRows)
        {
            result.Rows.Add(new LegacyCertificateImportResultRow
            {
                CertificateNumber = row.CertificateNumber,
                TraineeNameArabic = row.FullNameArabic,
                TraineeNameEnglish = row.FullNameEnglish,
                CourseName = row.CourseName,
                Imported = false,
                SkipReason = string.Join("، ", row.Errors)
            });
            result.Skipped++;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        await _audit.LogAsync(
            actor,
            "ImportLegacyCertificates",
            "Certificate",
            null,
            null,
            $"Imported: {result.Imported}; Skipped: {result.Skipped}; CoursesCreated: {result.CoursesCreated}; CoursesLinked: {result.CoursesLinked}",
            ip);

        return result;
    }

    /// <summary>Courses already in the system whose Arabic or legacy name matches <paramref name="name"/>.</summary>
    public async Task<List<LegacyImportOption>> FindMatchingCoursesAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new List<LegacyImportOption>();

        var matches = await _db.Courses.AsNoTracking()
            .Where(c => c.CourseNameArabic == name || c.CourseName == name)
            .OrderBy(c => c.CourseId)
            .Select(c => new LegacyImportOption { Id = c.CourseId, Name = c.CourseNameArabic ?? c.CourseName })
            .ToListAsync();

        return matches;
    }

    /// <summary>Searches courses by (partial) name, for the "link to existing" picker.</summary>
    public async Task<List<LegacyImportOption>> SearchCoursesAsync(string? term, int take = 20)
    {
        var query = _db.Courses.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var like = term.Trim();
            query = query.Where(c => c.CourseNameArabic!.Contains(like) || c.CourseName.Contains(like) || c.CourseNameEnglish!.Contains(like));
        }

        return await query
            .OrderByDescending(c => c.CourseId)
            .Take(take)
            .Select(c => new LegacyImportOption { Id = c.CourseId, Name = c.CourseNameArabic ?? c.CourseName })
            .ToListAsync();
    }

    private static string CourseKey(string courseName)
        => CollapseSpaces(courseName).ToLowerInvariant();

    private static void Recalculate(LegacyCertificateImportPreview preview)
    {
        preview.ValidRows = preview.Rows.Count(r => r.IsValid);
        preview.DuplicateRows = preview.Rows.Count(r => r.IsDuplicate);
        preview.InvalidRows = preview.Rows.Count(r => !r.IsValid && !r.IsDuplicate);
    }

    private async Task<string> GenerateRequestNumberAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = $"LEG-{DateTime.Now:yyyy}-{Random.Shared.Next(100000, 999999)}";
            if (!await _db.Registrations.AnyAsync(r => r.RequestNumber == candidate))
                return candidate;
        }

        return $"LEG-{DateTime.Now:yyyy}-{Guid.NewGuid():N}"[..24];
    }

    private static string GenerateUniqueToken(HashSet<string> reserved, Func<string> factory)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var value = factory();
            if (reserved.Add(value))
                return value;
        }

        throw new InvalidOperationException("تعذر توليد معرف فريد للشهادة.");
    }

    private static string CollapseSpaces(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
