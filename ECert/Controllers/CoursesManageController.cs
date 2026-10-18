using ECert.Data;
using ECert.Models;
using ECert.Services;
using ECert.Services.Excel;
using ECert.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class CoursesManageController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;
    public CoursesManageController(ECertDbContext db, AuditLogService audit, IWebHostEnvironment env)
    { _db = db; _audit = audit; _env = env; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    private async Task LoadCourseFormOptions()
    {
        ViewBag.Categories = await _db.Categories.Where(c => c.IsActive).ToListAsync();
        ViewBag.Instructors = await _db.Instructors.Where(i => i.IsActive).ToListAsync();
        ViewBag.CertificateDesigns = await _db.CertificateDesigns
            .OrderByDescending(d => d.IsPublished)
            .ThenBy(d => d.Name)
            .ToListAsync();
    }

    private async Task<(string? Path, string? Error)> SaveImageAsync(IFormFile? image)
    {
        if (image == null || image.Length == 0) return (null, null);

        var (valid, error) = await ImageUploadValidator.ValidateAsync(image);
        if (!valid) return (null, error);

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "courses");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
        var filePath = Path.Combine(uploadsDir, fileName);
        using var stream = new FileStream(filePath, FileMode.Create);
        await image.CopyToAsync(stream);
        return ($"/uploads/courses/{fileName}", null);
    }

    public async Task<IActionResult> Index(string? search)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var query = IncludeCourseInstructors(_db.Courses.Include(c => c.Category).Include(c => c.Registrations)).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(c => (c.CourseNameArabic != null && c.CourseNameArabic.Contains(search)) || (c.CourseNameEnglish != null && c.CourseNameEnglish.Contains(search)) || c.CourseName.Contains(search));
        ViewBag.Search = search;
        var courses = await query.OrderByDescending(c => c.CreatedAt).ToListAsync();
        return View(courses);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (!HasPermission("manage-courses")) return Forbid();
        await LoadCourseFormOptions();
        return View(new Course());
    }

    [HttpGet]
    public async Task<IActionResult> Import()
    {
        if (!HasPermission("manage-courses")) return Forbid();
        return View(new CourseImportPageViewModel());
    }

    [HttpGet]
    public IActionResult DownloadImportTemplate()
    {
        if (!HasPermission("manage-courses")) return Forbid();

        using var export = new ExcelExport("الدورات",
            "اسم الدورة (عربي)", "اسم الدورة (إنجليزي)", "الفئة", "المدربون", "السعر",
            "نوع الخصم", "قيمة الخصم", "تاريخ البداية", "تاريخ النهاية", "المكان", "الحالة",
            "مميزة", "تتطلب بيانات أكاديمية", "الوصف المختصر", "الوصف الكامل", "الأهداف", "المحتوى");

        export.WriteSampleRow(
            "مثال: القيادة الإدارية الفعالة", "Effective Administrative Leadership",
            "اسم الفئة كما يظهر في النظام",
            "اسم المدرب كما يظهر في النظام، وبين كل اسم وآخر فاصلة",
            1200, "Percentage", 10,
            DateTime.Today.AddDays(7), DateTime.Today.AddDays(14), "الرياض", "مسودة", "لا", "لا",
            "وصف مختصر للدورة", "وصف كامل للدورة", "أهداف الدورة", "محتوى الدورة");

        return export
            .ColumnWidth(1, 34).ColumnWidth(2, 34).ColumnWidth(4, 42)
            .ColumnWidth(15, 34).ColumnWidth(16, 34).ColumnWidth(17, 34).ColumnWidth(18, 40)
            .Build("courses-import-template");
    }

    [HttpGet]
    public async Task<IActionResult> Export()
    {
        if (!HasPermission("manage-courses")) return Forbid();

        var courses = await _db.Courses
            .Include(c => c.Category)
            .Include(c => c.Instructor)
            .Include(c => c.CourseInstructors).ThenInclude(ci => ci.Instructor)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        using var export = new ExcelExport("الدورات",
            "اسم الدورة (عربي)", "اسم الدورة (إنجليزي)", "الفئة", "المدربون", "السعر",
            "نوع الخصم", "قيمة الخصم", "تاريخ البداية", "تاريخ النهاية", "المكان", "الحالة",
            "مميزة", "تتطلب بيانات أكاديمية", "الوصف المختصر", "الوصف الكامل", "الأهداف", "المحتوى");

        foreach (var course in courses)
        {
            export.WriteRow(
                course.CourseNameArabic ?? course.CourseName,
                course.CourseNameEnglish ?? course.CourseName,
                course.Category?.CategoryName,
                course.InstructorNamesDisplay,
                course.Price,
                course.DiscountType,
                course.DiscountValue,
                ExcelLabels.Date(course.StartDate),
                ExcelLabels.Date(course.EndDate),
                course.Location,
                ExcelLabels.CourseStatus(course.Status),
                ExcelLabels.Boolean(course.IsFeatured),
                ExcelLabels.Boolean(course.RequiresAcademicDetails),
                course.ShortDescription,
                course.FullDescription,
                course.Objectives,
                course.Content);
        }

        return export
            .ColumnWidth(1, 40).ColumnWidth(2, 34)
            .ColumnWidth(14, 34).ColumnWidth(15, 34).ColumnWidth(16, 34).ColumnWidth(17, 40)
            .Build("courses");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Import(IFormFile? file)
    {
        if (!HasPermission("manage-courses")) return Forbid();

        var model = new CourseImportPageViewModel();
        if (ExcelImport.ValidateUpload(file) is { } uploadError)
        {
            ModelState.AddModelError(nameof(model.File), uploadError);
            return View(model);
        }

        try
        {
            await using var stream = file!.OpenReadStream();
            var preview = await BuildImportPreview(stream);
            preview.CreatedAtUtc = DateTime.UtcNow;
            preview.Token = ExcelPreviewStore.Save("ecert-course-imports", preview);
            model.Preview = preview;
            return View(model);
        }
        catch (ExcelImportException ex)
        {
            ModelState.AddModelError(nameof(model.File), ex.Message);
            return View(model);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmImport(string token)
    {
        if (!HasPermission("manage-courses")) return Forbid();

        var preview = ExcelPreviewStore.Take<CourseImportPreview>("ecert-course-imports", token);
        if (preview is null)
        {
            TempData["Error"] = "انتهت صلاحية المعاينة. ارفع الملف مرة أخرى.";
            return RedirectToAction(nameof(Import));
        }
        if (ExcelPreviewStore.IsExpired(preview.CreatedAtUtc) || preview.InvalidRows > 0 || preview.ValidRows == 0)
        {
            TempData["Error"] = "لا يمكن اعتماد الملف. يجب معالجة جميع الأخطاء أولاً.";
            return RedirectToAction(nameof(Import));
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var names = preview.Rows.Select(r => Normalize(r.CourseName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var createdCourses = new List<(Course course, CourseImportRow row)>();
            var existingNames = (await _db.Courses.Select(c => c.CourseName).ToListAsync())
                .Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Any(existingNames.Contains))
            {
                TempData["Error"] = "توجد دورات بالاسم نفسه في النظام. لم يتم حفظ أي صف لتجنب التكرار.";
                return RedirectToAction(nameof(Import));
            }
            foreach (var row in preview.Rows)
            {
                var course = new Course
                {
                    CourseName = row.CourseName,
                    CourseNameEnglish = row.CourseNameEnglish ?? row.CourseName,
                    CourseNameArabic = row.CourseNameArabic ?? row.CourseName,
                    CategoryId = row.CategoryId!.Value,
                    InstructorId = row.InstructorIds.First(),
                    Price = row.Price,
                    DiscountType = row.DiscountType,
                    DiscountValue = row.DiscountValue,
                    StartDate = row.StartDate,
                    EndDate = row.EndDate,
                    Location = row.Location,
                    Status = row.Status,
                    IsFeatured = row.IsFeatured,
                    RequiresAcademicDetails = row.RequiresAcademicDetails,
                    ShortDescription = row.ShortDescription,
                    FullDescription = row.FullDescription,
                    Objectives = row.Objectives,
                    Content = row.Content,
                    TotalSeats = 0,
                    BookedSeats = 0,
                    CreatedAt = DateTime.Now
                };
                _db.Courses.Add(course);
                createdCourses.Add((course, row));
            }
            await _db.SaveChangesAsync();
            foreach (var item in createdCourses)
                await SyncCourseInstructorsAsync(item.course.CourseId, item.row.InstructorIds);
            await transaction.CommitAsync();
            await _audit.LogAsync(User.Identity?.Name ?? "", "BulkCreate", "Course", null, null, $"Imported {preview.ValidRows} courses");
            TempData["Success"] = $"تم استيراد {preview.ValidRows} دورة بنجاح دون أخطاء جزئية.";
        }
        catch
        {
            await transaction.RollbackAsync();
            TempData["Error"] = "فشل حفظ الاستيراد بالكامل، ولم يتم حفظ أي دورة.";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<CourseImportPreview> BuildImportPreview(Stream stream)
    {
        using var import = ExcelImport.Open(stream, ExcelColumns.CourseAliases);
        var missing = import.MissingColumns([ExcelColumns.CourseName, ExcelColumns.Category, ExcelColumns.Instructors, ExcelColumns.Price]);
        if (missing.Count > 0)
            throw new ExcelImportException($"أعمدة مفقودة في الملف: {string.Join("، ", missing.Select(DisplayColumn))}");

        var preview = new CourseImportPreview();
        var categories = await _db.Categories.Where(c => c.IsActive).ToDictionaryAsync(c => Normalize(c.CategoryName), c => c.CategoryId);
        var instructors = await _db.Instructors.Where(i => i.IsActive).ToDictionaryAsync(i => Normalize(i.FullName), i => i.InstructorId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var rowNumber = ExcelImport.FirstDataRow; rowNumber <= import.LastRow; rowNumber++)
        {
            var row = new CourseImportRow { RowNumber = rowNumber };
            row.CourseNameArabic = import.OptionalText(rowNumber, ExcelColumns.CourseName);
            row.CourseNameEnglish = import.OptionalText(rowNumber, ExcelColumns.CourseNameEnglish);
            row.CourseName = row.CourseNameArabic ?? row.CourseNameEnglish ?? string.Empty;
            row.Category = import.Text(rowNumber, ExcelColumns.Category);
            row.Instructors = import.Text(rowNumber, ExcelColumns.Instructors);
            row.ShortDescription = import.OptionalText(rowNumber, ExcelColumns.ShortDescription);
            row.FullDescription = import.OptionalText(rowNumber, ExcelColumns.FullDescription);
            row.Objectives = import.OptionalText(rowNumber, ExcelColumns.Objectives);
            row.Content = import.OptionalText(rowNumber, ExcelColumns.Content);
            row.Location = import.OptionalText(rowNumber, ExcelColumns.Location);
            row.Status = ExcelLabels.ParseCourseStatus(import.OptionalText(rowNumber, ExcelColumns.Status));
            row.DiscountType = ExcelLabels.ParseDiscountType(import.OptionalText(rowNumber, ExcelColumns.DiscountType));
            row.IsFeatured = import.Flag(rowNumber, ExcelColumns.IsFeatured);
            row.RequiresAcademicDetails = import.Flag(rowNumber, ExcelColumns.RequiresAcademicDetails);
            row.Price = import.Decimal(rowNumber, ExcelColumns.Price, "السعر", row.Errors);
            row.DiscountValue = import.Decimal(rowNumber, ExcelColumns.DiscountValue, "قيمة الخصم", row.Errors);
            row.StartDate = import.Date(rowNumber, ExcelColumns.StartDate, "تاريخ البداية", row.Errors);
            row.EndDate = import.Date(rowNumber, ExcelColumns.EndDate, "تاريخ النهاية", row.Errors);

            if (string.IsNullOrWhiteSpace(row.CourseName)) row.Errors.Add("اسم الدورة مطلوب.");
            else if (row.CourseName.Length > 200) row.Errors.Add("اسم الدورة يتجاوز 200 حرف.");
            if (string.IsNullOrWhiteSpace(import.Text(rowNumber, ExcelColumns.Price))) row.Errors.Add("السعر مطلوب.");

            if (!categories.TryGetValue(Normalize(row.Category), out var categoryId)) row.Errors.Add("الفئة غير موجودة أو غير نشطة.");
            else row.CategoryId = categoryId;

            var instructorNames = SplitInstructorNames(row.Instructors).ToList();
            if (instructorNames.Count == 0)
            {
                row.Errors.Add("يجب تحديد مدرب واحد على الأقل.");
            }
            else
            {
                foreach (var instructorName in instructorNames)
                {
                    if (!instructors.TryGetValue(Normalize(instructorName), out var instructorId))
                    {
                        row.Errors.Add($"المدرب غير موجود أو غير نشط: {instructorName}");
                        continue;
                    }
                    if (!row.InstructorIds.Contains(instructorId))
                        row.InstructorIds.Add(instructorId);
                }
            }

            if (!string.IsNullOrWhiteSpace(row.CourseName) && !seen.Add(Normalize(row.CourseName)))
                row.Errors.Add("اسم الدورة مكرر داخل الملف.");
            if (row.Price < 0) row.Errors.Add("السعر لا يمكن أن يكون سالباً.");
            if (row.DiscountValue < 0) row.Errors.Add("الخصم لا يمكن أن يكون سالباً.");
            if (row.DiscountType is not (null or "" or "Percentage" or "Fixed")) row.Errors.Add("نوع الخصم يجب أن يكون نسبة مئوية أو مبلغاً ثابتاً.");
            if (row.DiscountType == "Percentage" && row.DiscountValue > 100) row.Errors.Add("الخصم النسبي لا يمكن أن يتجاوز 100%.");
            if (row.StartDate.HasValue && row.EndDate.HasValue && row.EndDate < row.StartDate) row.Errors.Add("تاريخ النهاية قبل تاريخ البداية.");
            if (!ExcelLabels.IsKnownCourseStatus(row.Status)) row.Errors.Add("حالة الدورة غير معروفة.");

            preview.Rows.Add(row);
        }

        preview.TotalRows = preview.Rows.Count;
        preview.ValidRows = preview.Rows.Count(r => r.IsValid);
        preview.InvalidRows = preview.TotalRows - preview.ValidRows;
        return preview;
    }

    private static string DisplayColumn(string key) => key switch
    {
        ExcelColumns.CourseName => "اسم الدورة",
        ExcelColumns.Category => "الفئة",
        ExcelColumns.Instructors => "المدربون",
        ExcelColumns.Price => "السعر",
        _ => key
    };

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static IEnumerable<string> SplitInstructorNames(string? raw)
        => (raw ?? string.Empty)
            .Split(new[] { ',', '،', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IQueryable<Course> IncludeCourseInstructors(IQueryable<Course> query)
        => query.Include(c => c.Instructor)
            .Include(c => c.CourseInstructors)
            .ThenInclude(ci => ci.Instructor);

    private static List<int> NormalizeInstructorIds(IEnumerable<int>? instructorIds)
        => (instructorIds ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();

    private async Task ValidateSelectedInstructorsAsync(IReadOnlyCollection<int> instructorIds)
    {
        if (instructorIds.Count == 0)
        {
            ModelState.AddModelError(nameof(Course.SelectedInstructorIds), "اختر مدرباً واحداً على الأقل.");
            return;
        }

        var activeCount = await _db.Instructors.CountAsync(i => i.IsActive && instructorIds.Contains(i.InstructorId));
        if (activeCount != instructorIds.Count)
            ModelState.AddModelError(nameof(Course.SelectedInstructorIds), "بعض المدربين المحددين غير موجودين أو غير نشطين.");
    }

    private async Task SyncCourseInstructorsAsync(int courseId, IReadOnlyList<int> instructorIds)
    {
        var normalizedIds = NormalizeInstructorIds(instructorIds);
        var existingRows = await _db.CourseInstructors.Where(ci => ci.CourseId == courseId).ToListAsync();
        var incoming = normalizedIds.ToHashSet();

        var toRemove = existingRows.Where(ci => !incoming.Contains(ci.InstructorId)).ToList();
        if (toRemove.Count > 0)
            _db.CourseInstructors.RemoveRange(toRemove);

        for (var index = 0; index < normalizedIds.Count; index++)
        {
            var instructorId = normalizedIds[index];
            var row = existingRows.FirstOrDefault(ci => ci.InstructorId == instructorId);
            if (row == null)
            {
                _db.CourseInstructors.Add(new CourseInstructor
                {
                    CourseId = courseId,
                    InstructorId = instructorId,
                    SortOrder = index
                });
            }
            else
            {
                row.SortOrder = index;
            }
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Course course, IFormFile? image)
    {
        if (!HasPermission("manage-courses")) return Forbid();

        course.CourseNameEnglish = course.CourseNameEnglish?.Trim() ?? string.Empty;
        course.CourseNameArabic = course.CourseNameArabic?.Trim() ?? string.Empty;
        course.CourseName = course.CourseNameArabic;
        course.SelectedInstructorIds = NormalizeInstructorIds(course.SelectedInstructorIds);
        await ValidateSelectedInstructorsAsync(course.SelectedInstructorIds);
        if (course.SelectedInstructorIds.Count > 0)
            course.InstructorId = course.SelectedInstructorIds[0];

        if (!ModelState.IsValid)
        {
            await LoadCourseFormOptions();
            return View(course);
        }

        course.CreatedAt = DateTime.Now;
        var (imgPath, imgError) = await SaveImageAsync(image);
        if (imgError != null)
        {
            ModelState.AddModelError(string.Empty, imgError);
            await LoadCourseFormOptions();
            return View(course);
        }
        if (imgPath != null) course.ImageUrl = imgPath;
        _db.Courses.Add(course);
        await _db.SaveChangesAsync();
        await SyncCourseInstructorsAsync(course.CourseId, course.SelectedInstructorIds);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Course", course.CourseId, null, $"{course.CourseNameEnglish} / {course.CourseNameArabic}");
        TempData["Success"] = "تمت إضافة الدورة بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var course = await _db.Courses
            .Include(c => c.Instructor)
            .Include(c => c.CourseInstructors).ThenInclude(ci => ci.Instructor)
            .FirstOrDefaultAsync(c => c.CourseId == id);
        if (course == null) return NotFound();
        course.SelectedInstructorIds = course.CourseInstructors
            .OrderBy(ci => ci.SortOrder)
            .ThenBy(ci => ci.CourseInstructorId)
            .Select(ci => ci.InstructorId)
            .Distinct()
            .ToList();
        if (course.SelectedInstructorIds.Count == 0 && course.InstructorId > 0)
            course.SelectedInstructorIds.Add(course.InstructorId);
        await LoadCourseFormOptions();
        return View(course);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Course course, IFormFile? image)
    {
        if (!HasPermission("manage-courses")) return Forbid();

        course.CourseNameEnglish = course.CourseNameEnglish?.Trim() ?? string.Empty;
        course.CourseNameArabic = course.CourseNameArabic?.Trim() ?? string.Empty;
        course.CourseName = course.CourseNameArabic;
        course.SelectedInstructorIds = NormalizeInstructorIds(course.SelectedInstructorIds);
        await ValidateSelectedInstructorsAsync(course.SelectedInstructorIds);
        if (course.SelectedInstructorIds.Count > 0)
            course.InstructorId = course.SelectedInstructorIds[0];

        if (!ModelState.IsValid)
        {
            await LoadCourseFormOptions();
            return View(course);
        }

        var existing = await _db.Courses.FindAsync(course.CourseId);
        if (existing == null) return NotFound();

        existing.CourseName = course.CourseNameArabic;
        existing.CourseNameEnglish = course.CourseNameEnglish;
        existing.CourseNameArabic = course.CourseNameArabic;
        existing.ShortDescription = course.ShortDescription;
        existing.FullDescription = course.FullDescription;
        existing.Objectives = course.Objectives;
        existing.Content = course.Content;
        existing.CategoryId = course.CategoryId;
        existing.InstructorId = course.InstructorId;
        existing.CertificateDesignId = course.CertificateDesignId;
        existing.StartDate = course.StartDate;
        existing.EndDate = course.EndDate;
        existing.Location = course.Location;
        existing.Price = course.Price;
        existing.DiscountType = course.DiscountType;
        existing.DiscountValue = course.DiscountValue;
        existing.Status = course.Status;
        existing.IsFeatured = course.IsFeatured;
        existing.RequiresAcademicDetails = course.RequiresAcademicDetails;
        var (imgPath, imgError) = await SaveImageAsync(image);
        if (imgError != null)
        {
            ModelState.AddModelError(string.Empty, imgError);
            await LoadCourseFormOptions();
            return View(existing);
        }
        if (imgPath != null)
        {
            if (!string.IsNullOrEmpty(existing.ImageUrl))
            {
                var oldImgPath = Path.Combine(_env.WebRootPath, existing.ImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(oldImgPath))
                    System.IO.File.Delete(oldImgPath);
            }
            existing.ImageUrl = imgPath;
        }

        await SyncCourseInstructorsAsync(existing.CourseId, course.SelectedInstructorIds);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "Course", course.CourseId, null, $"{course.CourseNameEnglish} / {course.CourseNameArabic}");
        TempData["Success"] = "تم تعديل الدورة بنجاح.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStatus(int id, string status)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var course = await _db.Courses.FindAsync(id);
        if (course == null) return NotFound();
        course.Status = status;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Update", "Course", id, null, $"Status: {status}");
        TempData["Success"] = "تم تغيير حالة الدورة.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!HasPermission("manage-courses")) return Forbid();
        var course = await _db.Courses.FindAsync(id);
        if (course == null) return NotFound();

        // Delete image file from disk
        if (!string.IsNullOrEmpty(course.ImageUrl))
        {
            var imgPath = Path.Combine(_env.WebRootPath, course.ImageUrl.TrimStart('/'));
            if (System.IO.File.Exists(imgPath))
                System.IO.File.Delete(imgPath);
        }

        course.Status = "Archived";
        course.ImageUrl = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Delete", "Course", id);
        TempData["Success"] = "تم أرشفة الدورة.";
        return RedirectToAction("Index");
    }
}
