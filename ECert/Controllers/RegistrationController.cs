using ECert.Models;
using ECert.Data;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

public class RegistrationController : Controller
{
    private readonly ECertDbContext _db;
    private readonly TurnstileService _turnstile;

    public RegistrationController(ECertDbContext db, TurnstileService turnstile)
    {
        _db = db;
        _turnstile = turnstile;
    }

    [HttpGet]
    public async Task<IActionResult> Register(int courseId)
    {
        var course = await _db.Courses.FindAsync(courseId);
        if (course == null) return NotFound();
        if (course.Status != "OpenForRegistration")
        {
            TempData["Error"] = "التسجيل غير متاح لهذه الدورة حالياً.";
            return RedirectToAction("Details", "PublicCourses", new { id = courseId });
        }
        var vm = new PublicRegistrationViewModel
        {
            CourseId = courseId,
            CourseName = course.CourseNameArabic,
            CourseNameEnglish = course.CourseNameEnglish,
            CourseNameArabic = course.CourseNameArabic,
            IncludeAcademicDetails = course.RequiresAcademicDetails
        };
        await PopulateRegistrationFormAsync(course);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("public-registration")]
    public async Task<IActionResult> Register(PublicRegistrationViewModel model)
    {
        var course = await _db.Courses.FindAsync(model.CourseId);
        if (course == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(model.WebsiteUrl))
        {
            // Silently accept the request without persisting bot submissions.
            return View("Success");
        }

        if (!await _turnstile.VerifyRequestAsync(HttpContext))
        {
            ModelState.AddModelError(string.Empty, "تعذر التحقق من أنك مستخدم حقيقي. أعد المحاولة بعد اكتمال التحقق.");
            return await ReturnFormAsync(model, course);
        }

        if (model.Gender is not ("Male" or "Female"))
            ModelState.AddModelError(nameof(model.Gender), "اختر الجنس من القائمة.");
        if (!ModelState.IsValid)
            return await ReturnFormAsync(model, course);

        var country = await _db.PhoneCountries.FindAsync(model.CountryId);
        if (country == null || !country.IsActive)
        {
            ModelState.AddModelError(nameof(model.CountryId), "الدولة المختارة غير صحيحة.");
            return await ReturnFormAsync(model, course);
        }

        var phone = model.Phone.Trim();
        if (phone.StartsWith("0")) phone = phone[1..];
        if (phone.Length < country.MinPhoneLength || phone.Length > country.MaxPhoneLength)
        {
            ModelState.AddModelError(nameof(model.Phone), $"رقم الهاتف يجب أن يكون بين {country.MinPhoneLength} و {country.MaxPhoneLength} أرقام.");
            return await ReturnFormAsync(model, course);
        }
        if (!phone.All(char.IsDigit))
        {
            ModelState.AddModelError(nameof(model.Phone), "رقم الهاتف يجب أن يحتوي على أرقام فقط.");
            return await ReturnFormAsync(model, course);
        }
        if (!string.IsNullOrEmpty(country.Prefixes))
        {
            var allowedPrefixes = country.Prefixes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowedPrefixes.Length > 0 && !allowedPrefixes.Any(p => phone.StartsWith(p)))
            {
                ModelState.AddModelError(nameof(model.Phone), $"رقم الهاتف يجب أن يبدأ بأحد البادئات: {string.Join(", ", allowedPrefixes)}.");
                return await ReturnFormAsync(model, course);
            }
        }

        var normalizedPhone = $"{country.CountryCode}{phone}";
        var duplicateExists = await _db.Registrations.AsNoTracking().AnyAsync(r =>
            r.CourseId == model.CourseId &&
            r.Phone == normalizedPhone &&
            r.Status != "Rejected" &&
            r.Status != "Archived");
        if (duplicateExists)
        {
            ModelState.AddModelError(nameof(model.Phone), "يوجد تسجيل قائم بهذا الرقم لهذه الدورة بالفعل.");
            return await ReturnFormAsync(model, course);
        }

        var hasAnyAcademicValue = model.UniversityId.HasValue || model.CollegeId.HasValue ||
                                  model.AcademicSpecializationId.HasValue || !string.IsNullOrWhiteSpace(model.AcademicLevel);
        var needsAcademicDetails = course.RequiresAcademicDetails || model.IncludeAcademicDetails || hasAnyAcademicValue;
        AcademicSelection? academic = null;
        if (needsAcademicDetails)
        {
            academic = await ValidateAcademicSelectionAsync(model);
            if (academic == null) return await ReturnFormAsync(model, course);
        }

        var requestNumber = $"REG-{DateTime.Now:yyyy}-{new Random().Next(10000, 99999)}";
        var registration = new Registration
        {
            RequestNumber = requestNumber,
            FullName = model.FullNameArabic!.Trim(),
            FullNameArabic = model.FullNameArabic.Trim(),
            FullNameEnglish = model.FullNameEnglish!.Trim(),
            Gender = model.Gender,
            Phone = normalizedPhone,
            Email = model.Email?.Trim(),
            HeardFrom = model.HeardFrom?.Trim(),
            CourseId = model.CourseId,
            Status = "Pending",
            RegistrationDate = DateTime.Now,
            UniversityId = academic?.University.UniversityId,
            CollegeId = academic?.College.CollegeId,
            AcademicSpecializationId = academic?.Specialization.AcademicSpecializationId,
            AcademicLevel = academic?.AcademicLevel,
            UniversityNameSnapshot = academic?.University.UniversityName,
            CollegeNameSnapshot = academic?.College.CollegeName,
            SpecializationNameSnapshot = academic?.Specialization.SpecializationName
        };

        _db.Registrations.Add(registration);
        await _db.SaveChangesAsync();

        ViewBag.RequestNumber = requestNumber;
        ViewBag.FullName = model.FullNameArabic;
        ViewBag.FullNameArabic = model.FullNameArabic;
        ViewBag.FullNameEnglish = model.FullNameEnglish;
        ViewBag.CourseName = course.CourseNameArabic;
        ViewBag.CourseNameEnglish = course.CourseNameEnglish;
        ViewBag.CourseNameArabic = course.CourseNameArabic;
        return View("Success");
    }

    private async Task<AcademicSelection?> ValidateAcademicSelectionAsync(PublicRegistrationViewModel model)
    {
        if (!model.UniversityId.HasValue)
            ModelState.AddModelError(nameof(model.UniversityId), "اختر الجامعة من القائمة.");
        if (!model.CollegeId.HasValue)
            ModelState.AddModelError(nameof(model.CollegeId), "اختر الكلية من القائمة.");
        if (!model.AcademicSpecializationId.HasValue)
            ModelState.AddModelError(nameof(model.AcademicSpecializationId), "اختر التخصص من القائمة.");

        var level = model.AcademicLevel?.Trim();
        if (string.IsNullOrEmpty(level))
            ModelState.AddModelError(nameof(model.AcademicLevel), "اختر المستوى الدراسي من القائمة.");

        if (!ModelState.IsValid || !model.UniversityId.HasValue || !model.CollegeId.HasValue || !model.AcademicSpecializationId.HasValue)
            return null;

        var university = await _db.Universities.SingleOrDefaultAsync(u => u.UniversityId == model.UniversityId && u.IsActive);
        if (university == null)
        {
            ModelState.AddModelError(nameof(model.UniversityId), "الجامعة المختارة غير متاحة.");
            return null;
        }

        var college = await _db.Colleges.Include(c => c.University)
            .SingleOrDefaultAsync(c => c.CollegeId == model.CollegeId && c.UniversityId == university.UniversityId && c.IsActive && c.University!.IsActive);
        if (college == null)
        {
            ModelState.AddModelError(nameof(model.CollegeId), "الكلية المختارة لا تنتمي إلى الجامعة أو غير متاحة.");
            return null;
        }

        var specialization = await _db.AcademicSpecializations.Include(s => s.College).ThenInclude(c => c!.University)
            .SingleOrDefaultAsync(s => s.AcademicSpecializationId == model.AcademicSpecializationId && s.CollegeId == college.CollegeId && s.IsActive && s.College!.IsActive && s.College.University!.IsActive);
        if (specialization == null)
        {
            ModelState.AddModelError(nameof(model.AcademicSpecializationId), "التخصص المختار لا ينتمي إلى الكلية أو غير متاح.");
            return null;
        }

        var levelIsValid = await _db.AcademicLevelOptions.AnyAsync(l => l.AcademicSpecializationId == specialization.AcademicSpecializationId && l.LevelName == level && l.IsActive);
        if (!levelIsValid)
        {
            ModelState.AddModelError(nameof(model.AcademicLevel), "المستوى المختار غير متاح لهذا التخصص.");
            return null;
        }

        return new AcademicSelection(university, college, specialization, level!);
    }

    private async Task<IActionResult> ReturnFormAsync(PublicRegistrationViewModel model, Course course)
    {
        model.CourseName = course.CourseNameArabic;
        model.CourseNameEnglish = course.CourseNameEnglish;
        model.CourseNameArabic = course.CourseNameArabic;
        model.IncludeAcademicDetails = course.RequiresAcademicDetails || model.IncludeAcademicDetails;
        await PopulateAcademicDisplayNamesAsync(model);
        await PopulateRegistrationFormAsync(course);
        return View(model);
    }

    private async Task PopulateRegistrationFormAsync(Course course)
    {
        ViewBag.Countries = await _db.PhoneCountries.Where(c => c.IsActive).OrderBy(c => c.CountryName).ToListAsync();
        ViewBag.RequiresAcademicDetails = course.RequiresAcademicDetails;
        ViewBag.TurnstileSiteKey = _turnstile.SiteKey;
    }

    private async Task PopulateAcademicDisplayNamesAsync(PublicRegistrationViewModel model)
    {
        if (model.UniversityId.HasValue)
            model.UniversityName = await _db.Universities.Where(u => u.UniversityId == model.UniversityId).Select(u => u.UniversityName).FirstOrDefaultAsync();
        if (model.CollegeId.HasValue)
            model.CollegeName = await _db.Colleges.Where(c => c.CollegeId == model.CollegeId).Select(c => c.CollegeName).FirstOrDefaultAsync();
        if (model.AcademicSpecializationId.HasValue)
            model.SpecializationName = await _db.AcademicSpecializations.Where(s => s.AcademicSpecializationId == model.AcademicSpecializationId).Select(s => s.SpecializationName).FirstOrDefaultAsync();
    }

    private sealed record AcademicSelection(University University, College College, AcademicSpecialization Specialization, string AcademicLevel);
}
