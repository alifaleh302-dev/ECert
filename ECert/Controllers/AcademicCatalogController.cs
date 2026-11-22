using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class AcademicCatalogController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;

    public AcademicCatalogController(ECertDbContext db, AuditLogService audit)
    {
        _db = db;
        _audit = audit;
    }

    private bool CanManage() => User.HasPermission(AdminPermissions.ManageAcademicCatalog);

    /// <summary>
    /// Renders the catalog as one tree plus a detail panel. <paramref name="selected"/> is
    /// "kind:id" (for example <c>college:3</c>) and <paramref name="add"/> names the kind of child
    /// to create under <paramref name="parentId"/>, so selection and "add here" are plain links
    /// that survive a reload and work without JavaScript.
    /// </summary>
    public async Task<IActionResult> Index(string? selected = null, string? add = null, int? parentId = null)
    {
        if (!CanManage()) return Forbid();

        var universities = await _db.Universities.AsNoTracking().OrderBy(u => u.UniversityName).ToListAsync();
        var colleges = await _db.Colleges.AsNoTracking().OrderBy(c => c.CollegeName).ToListAsync();
        var specializations = await _db.AcademicSpecializations.AsNoTracking()
            .OrderBy(s => s.SpecializationName).ToListAsync();
        var levels = await _db.AcademicLevelOptions.AsNoTracking()
            .OrderBy(l => l.SortOrder).ThenBy(l => l.LevelName).ToListAsync();

        var nodes = new Dictionary<string, AcademicNode>(StringComparer.Ordinal);
        var roots = new List<AcademicNode>();

        foreach (var university in universities)
        {
            var node = new AcademicNode(AcademicNode.University, university.UniversityId, university.UniversityName, university.IsActive);
            roots.Add(node);
            nodes[node.Key] = node;
        }
        foreach (var college in colleges)
        {
            if (!nodes.TryGetValue(AcademicNode.MakeKey(AcademicNode.University, college.UniversityId), out var parent))
                continue;
            var node = new AcademicNode(AcademicNode.College, college.CollegeId, college.CollegeName, college.IsActive, parent);
            parent.Children.Add(node);
            nodes[node.Key] = node;
        }
        foreach (var specialization in specializations)
        {
            if (!nodes.TryGetValue(AcademicNode.MakeKey(AcademicNode.College, specialization.CollegeId), out var parent))
                continue;
            var node = new AcademicNode(AcademicNode.Specialization, specialization.AcademicSpecializationId,
                specialization.SpecializationName, specialization.IsActive, parent);
            parent.Children.Add(node);
            nodes[node.Key] = node;
        }
        foreach (var level in levels)
        {
            if (!nodes.TryGetValue(AcademicNode.MakeKey(AcademicNode.Specialization, level.AcademicSpecializationId), out var parent))
                continue;
            var node = new AcademicNode(AcademicNode.Level, level.AcademicLevelOptionId, level.LevelName,
                level.IsActive, parent, level.SortOrder);
            parent.Children.Add(node);
            nodes[node.Key] = node;
        }

        var selectedNode = selected is not null && nodes.TryGetValue(selected, out var match)
            ? match
            : roots.FirstOrDefault();

        // Only the path to the selection is expanded on first paint; the rest is one click away.
        for (var node = selectedNode; node is not null; node = node.Parent)
            node.IsOpen = true;

        var parentKind = AcademicNode.ParentKindOf(add);
        AcademicNode? addParent = null;
        if (parentKind is not null && parentId is int id)
            nodes.TryGetValue(AcademicNode.MakeKey(parentKind, id), out addParent);

        var addKind = add is not null && AcademicNode.IsKind(add)
            && (add == AcademicNode.University || addParent is not null)
                ? add
                : null;

        return View(new AcademicCatalogPageViewModel
        {
            Roots = roots,
            Selected = selectedNode,
            AddKind = addKind,
            AddParent = addParent,
            UniversityCount = universities.Count,
            CollegeCount = colleges.Count,
            SpecializationCount = specializations.Count,
            LevelCount = levels.Count
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUniversity(string universityName)
    {
        if (!CanManage()) return Forbid();
        universityName = NormalizeName(universityName);
        if (string.IsNullOrEmpty(universityName)) return CatalogError("اسم الجامعة مطلوب.");
        if (universityName.Length > 160) return CatalogError("اسم الجامعة لا يمكن أن يتجاوز 160 حرفاً.");
        if (await _db.Universities.AnyAsync(u => u.UniversityName == universityName)) return CatalogError("هذه الجامعة موجودة بالفعل.");

        var university = new University { UniversityName = universityName };
        _db.Universities.Add(university);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Create", "University", university.UniversityId, null, university.UniversityName);
        TempData["Success"] = "تمت إضافة الجامعة بنجاح.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.University, university.UniversityId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUniversity(int universityId, string universityName, bool isActive)
    {
        if (!CanManage()) return Forbid();
        var university = await _db.Universities.FindAsync(universityId);
        if (university == null) return NotFound();
        universityName = NormalizeName(universityName);
        if (string.IsNullOrEmpty(universityName)) return CatalogError("اسم الجامعة مطلوب.");
        if (await _db.Universities.AnyAsync(u => u.UniversityId != universityId && u.UniversityName == universityName)) return CatalogError("يوجد اسم جامعة مماثل بالفعل.");

        university.UniversityName = universityName;
        university.IsActive = isActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Update", "University", universityId, null, universityName);
        TempData["Success"] = "تم تحديث بيانات الجامعة.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.University, universityId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCollege(int universityId, string collegeName)
    {
        if (!CanManage()) return Forbid();
        collegeName = NormalizeName(collegeName);
        if (string.IsNullOrEmpty(collegeName)) return CatalogError("اسم الكلية مطلوب.");
        if (!await _db.Universities.AnyAsync(u => u.UniversityId == universityId)) return CatalogError("الجامعة المختارة غير موجودة.");
        if (await _db.Colleges.AnyAsync(c => c.UniversityId == universityId && c.CollegeName == collegeName)) return CatalogError("هذه الكلية موجودة بالفعل في الجامعة المحددة.");

        var college = new College { UniversityId = universityId, CollegeName = collegeName };
        _db.Colleges.Add(college);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Create", "College", college.CollegeId, null, college.CollegeName);
        TempData["Success"] = "تمت إضافة الكلية بنجاح.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.College, college.CollegeId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCollege(int collegeId, int universityId, string collegeName, bool isActive)
    {
        if (!CanManage()) return Forbid();
        var college = await _db.Colleges.FindAsync(collegeId);
        if (college == null) return NotFound();
        collegeName = NormalizeName(collegeName);
        if (string.IsNullOrEmpty(collegeName)) return CatalogError("اسم الكلية مطلوب.");
        if (!await _db.Universities.AnyAsync(u => u.UniversityId == universityId)) return CatalogError("الجامعة المختارة غير موجودة.");
        if (await _db.Colleges.AnyAsync(c => c.CollegeId != collegeId && c.UniversityId == universityId && c.CollegeName == collegeName)) return CatalogError("هذه الكلية موجودة بالفعل في الجامعة المحددة.");

        college.UniversityId = universityId;
        college.CollegeName = collegeName;
        college.IsActive = isActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Update", "College", collegeId, null, collegeName);
        TempData["Success"] = "تم تحديث بيانات الكلية.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.College, collegeId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSpecialization(int collegeId, string specializationName)
    {
        if (!CanManage()) return Forbid();
        specializationName = NormalizeName(specializationName);
        if (string.IsNullOrEmpty(specializationName)) return CatalogError("اسم التخصص مطلوب.");
        if (!await _db.Colleges.AnyAsync(c => c.CollegeId == collegeId)) return CatalogError("الكلية المختارة غير موجودة.");
        if (await _db.AcademicSpecializations.AnyAsync(s => s.CollegeId == collegeId && s.SpecializationName == specializationName)) return CatalogError("هذا التخصص موجود بالفعل في الكلية المحددة.");

        var specialization = new AcademicSpecialization { CollegeId = collegeId, SpecializationName = specializationName };
        _db.AcademicSpecializations.Add(specialization);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Create", "AcademicSpecialization", specialization.AcademicSpecializationId, null, specialization.SpecializationName);
        TempData["Success"] = "تمت إضافة التخصص بنجاح.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.Specialization, specialization.AcademicSpecializationId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSpecialization(int academicSpecializationId, int collegeId, string specializationName, bool isActive)
    {
        if (!CanManage()) return Forbid();
        var specialization = await _db.AcademicSpecializations.FindAsync(academicSpecializationId);
        if (specialization == null) return NotFound();
        specializationName = NormalizeName(specializationName);
        if (string.IsNullOrEmpty(specializationName)) return CatalogError("اسم التخصص مطلوب.");
        if (!await _db.Colleges.AnyAsync(c => c.CollegeId == collegeId)) return CatalogError("الكلية المختارة غير موجودة.");
        if (await _db.AcademicSpecializations.AnyAsync(s => s.AcademicSpecializationId != academicSpecializationId && s.CollegeId == collegeId && s.SpecializationName == specializationName)) return CatalogError("هذا التخصص موجود بالفعل في الكلية المحددة.");

        specialization.CollegeId = collegeId;
        specialization.SpecializationName = specializationName;
        specialization.IsActive = isActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Update", "AcademicSpecialization", academicSpecializationId, null, specializationName);
        TempData["Success"] = "تم تحديث بيانات التخصص.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.Specialization, academicSpecializationId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLevel(int academicSpecializationId, string levelName, int sortOrder = 0)
    {
        if (!CanManage()) return Forbid();
        levelName = NormalizeName(levelName);
        if (string.IsNullOrEmpty(levelName)) return CatalogError("اسم المستوى مطلوب.");
        if (levelName.Length > 80) return CatalogError("اسم المستوى لا يمكن أن يتجاوز 80 حرفاً.");
        var specialization = await _db.AcademicSpecializations.FindAsync(academicSpecializationId);
        if (specialization == null) return CatalogError("التخصص المختار غير موجود.");
        if (await _db.AcademicLevelOptions.AnyAsync(l => l.AcademicSpecializationId == academicSpecializationId && l.LevelName == levelName))
            return CatalogError("هذا المستوى موجود بالفعل لهذا التخصص.");

        var level = new AcademicLevelOption { AcademicSpecializationId = academicSpecializationId, LevelName = levelName, SortOrder = Math.Max(0, sortOrder) };
        _db.AcademicLevelOptions.Add(level);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Create", "AcademicLevelOption", level.AcademicLevelOptionId, null, levelName);
        TempData["Success"] = "تمت إضافة المستوى للتخصص بنجاح.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.Level, level.AcademicLevelOptionId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLevel(int academicLevelOptionId, int academicSpecializationId, string levelName, int sortOrder, bool isActive)
    {
        if (!CanManage()) return Forbid();
        var level = await _db.AcademicLevelOptions.FindAsync(academicLevelOptionId);
        if (level == null) return NotFound();
        levelName = NormalizeName(levelName);
        if (string.IsNullOrEmpty(levelName)) return CatalogError("اسم المستوى مطلوب.");
        if (!await _db.AcademicSpecializations.AnyAsync(s => s.AcademicSpecializationId == academicSpecializationId)) return CatalogError("التخصص المختار غير موجود.");
        if (await _db.AcademicLevelOptions.AnyAsync(l => l.AcademicLevelOptionId != academicLevelOptionId && l.AcademicSpecializationId == academicSpecializationId && l.LevelName == levelName))
            return CatalogError("هذا المستوى موجود بالفعل لهذا التخصص.");

        level.AcademicSpecializationId = academicSpecializationId;
        level.LevelName = levelName;
        level.SortOrder = Math.Max(0, sortOrder);
        level.IsActive = isActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Update", "AcademicLevelOption", academicLevelOptionId, null, levelName);
        TempData["Success"] = "تم تحديث المستوى الدراسي.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.Level, academicLevelOptionId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLevel(int id)
    {
        if (!CanManage()) return Forbid();
        var level = await _db.AcademicLevelOptions.FindAsync(id);
        if (level == null) return NotFound();
        _db.AcademicLevelOptions.Remove(level);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Delete", "AcademicLevelOption", id);
        TempData["Success"] = "تم حذف المستوى الدراسي.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.Specialization, level.AcademicSpecializationId) });
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Levels(int specializationId, string? q)
    {
        var query = NormalizeName(q ?? string.Empty);
        var items = await _db.AcademicLevelOptions
            .Where(l => l.AcademicSpecializationId == specializationId && l.IsActive && l.AcademicSpecialization!.IsActive && (string.IsNullOrEmpty(query) || l.LevelName.Contains(query)))
            .OrderBy(l => l.SortOrder).ThenBy(l => l.LevelName)
            .Select(l => new { id = l.AcademicLevelOptionId, name = l.LevelName })
            .ToListAsync();
        return Json(items);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUniversity(int id)
    {
        if (!CanManage()) return Forbid();
        var university = await _db.Universities.FindAsync(id);
        if (university == null) return NotFound();
        if (await _db.Colleges.AnyAsync(c => c.UniversityId == id) || await _db.Registrations.AnyAsync(r => r.UniversityId == id))
            return CatalogError("لا يمكن حذف الجامعة لأنها مستخدمة في كليات أو تسجيلات. يمكنك إيقافها بدلاً من ذلك.");
        _db.Universities.Remove(university);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Delete", "University", id);
        TempData["Success"] = "تم حذف الجامعة.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCollege(int id)
    {
        if (!CanManage()) return Forbid();
        var college = await _db.Colleges.FindAsync(id);
        if (college == null) return NotFound();
        if (await _db.AcademicSpecializations.AnyAsync(s => s.CollegeId == id) || await _db.Registrations.AnyAsync(r => r.CollegeId == id))
            return CatalogError("لا يمكن حذف الكلية لأنها مستخدمة في تخصصات أو تسجيلات. يمكنك إيقافها بدلاً من ذلك.");
        _db.Colleges.Remove(college);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Delete", "College", id);
        TempData["Success"] = "تم حذف الكلية.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.University, college.UniversityId) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSpecialization(int id)
    {
        if (!CanManage()) return Forbid();
        var specialization = await _db.AcademicSpecializations.FindAsync(id);
        if (specialization == null) return NotFound();
        if (await _db.Registrations.AnyAsync(r => r.AcademicSpecializationId == id))
            return CatalogError("لا يمكن حذف التخصص لأنه مستخدم في تسجيلات. يمكنك إيقافه بدلاً من ذلك.");
        _db.AcademicSpecializations.Remove(specialization);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? string.Empty, "Delete", "AcademicSpecialization", id);
        TempData["Success"] = "تم حذف التخصص.";
        return RedirectToAction(nameof(Index), new { selected = AcademicNode.MakeKey(AcademicNode.College, specialization.CollegeId) });
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Universities(string? q)
    {
        var query = NormalizeName(q ?? string.Empty);
        var items = await _db.Universities
            .Where(u => u.IsActive && (string.IsNullOrEmpty(query) || u.UniversityName.Contains(query)))
            .OrderBy(u => u.UniversityName)
            .Take(15)
            .Select(u => new { id = u.UniversityId, name = u.UniversityName })
            .ToListAsync();
        return Json(items);
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Colleges(int universityId, string? q)
    {
        var query = NormalizeName(q ?? string.Empty);
        var items = await _db.Colleges
            .Where(c => c.UniversityId == universityId && c.IsActive && c.University!.IsActive && (string.IsNullOrEmpty(query) || c.CollegeName.Contains(query)))
            .OrderBy(c => c.CollegeName)
            .Take(15)
            .Select(c => new { id = c.CollegeId, name = c.CollegeName })
            .ToListAsync();
        return Json(items);
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Specializations(int collegeId, string? q)
    {
        var query = NormalizeName(q ?? string.Empty);
        var items = await _db.AcademicSpecializations
            .Where(s => s.CollegeId == collegeId && s.IsActive && s.College!.IsActive && s.College.University!.IsActive && (string.IsNullOrEmpty(query) || s.SpecializationName.Contains(query)))
            .OrderBy(s => s.SpecializationName)
            .Take(15)
            .Select(s => new { id = s.AcademicSpecializationId, name = s.SpecializationName })
            .ToListAsync();
        return Json(items);
    }

    private IActionResult CatalogError(string message)
    {
        TempData["Error"] = message;
        return RedirectToAction(nameof(Index));
    }

    private static string NormalizeName(string value)
        => string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public class AcademicCatalogPageViewModel
{
    public List<AcademicNode> Roots { get; init; } = new();
    public AcademicNode? Selected { get; init; }
    public string? AddKind { get; init; }
    public AcademicNode? AddParent { get; init; }
    public int UniversityCount { get; init; }
    public int CollegeCount { get; init; }
    public int SpecializationCount { get; init; }
    public int LevelCount { get; init; }
}

/// <summary>
/// One node of the university → college → specialization → level tree, built once per request so
/// the view, the tree partial and the tests share the same shape instead of re-deriving parents.
/// </summary>
public sealed class AcademicNode
{
    public const string University = "university";
    public const string College = "college";
    public const string Specialization = "specialization";
    public const string Level = "level";

    public AcademicNode(string kind, int id, string name, bool isActive,
        AcademicNode? parent = null, int sortOrder = 0)
    {
        Kind = kind;
        Id = id;
        Name = name;
        IsActive = isActive;
        Parent = parent;
        SortOrder = sortOrder;
    }

    public string Kind { get; }
    public int Id { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public int SortOrder { get; }
    public AcademicNode? Parent { get; }
    public List<AcademicNode> Children { get; } = new();
    public bool IsOpen { get; set; }

    public string Key => MakeKey(Kind, Id);

    public static string MakeKey(string kind, int id) => $"{kind}:{id}";

    public static bool IsKind(string? kind)
        => kind is University or College or Specialization or Level;

    /// <summary>The kind of node this one can contain, or null for levels.</summary>
    public string? ChildKind => Kind switch
    {
        University => College,
        College => Specialization,
        Specialization => Level,
        _ => null
    };

    public static string? ParentKindOf(string? childKind) => childKind switch
    {
        College => University,
        Specialization => College,
        Level => Specialization,
        _ => null
    };

    public string Label => Kind switch
    {
        University => "جامعة",
        College => "كلية",
        Specialization => "تخصص",
        _ => "مستوى"
    };

    public string LabelDef => Kind switch
    {
        University => "الجامعة",
        College => "الكلية",
        Specialization => "التخصص",
        _ => "المستوى"
    };

    public string Plural => Kind switch
    {
        University => "الجامعات",
        College => "الكليات",
        Specialization => "التخصصات",
        _ => "المستويات"
    };

    public string ChildLabel => ChildKind switch
    {
        College => "كلية",
        Specialization => "تخصص",
        Level => "مستوى",
        _ => ""
    };

    public string ChildPlural => ChildKind switch
    {
        College => "الكليات",
        Specialization => "التخصصات",
        Level => "المستويات",
        _ => ""
    };

    public string Icon => Kind switch
    {
        University => "bi-building",
        College => "bi-bank",
        Specialization => "bi-mortarboard",
        _ => "bi-layers"
    };

    public string Path => Parent is null ? Name : $"{Parent.Path} / {Name}";

    /// <summary>"3 تخصصات" / "تخصص واحد" / "تخصصان" for this node's children.</summary>
    public string ChildrenCountText => Children.Count switch
    {
        0 => "",
        1 => ChildKind switch
        {
            College => "كلية واحدة",
            Specialization => "تخصص واحد",
            Level => "مستوى واحد",
            _ => "عنصر واحد"
        },
        2 => ChildKind switch
        {
            College => "كليتان",
            Specialization => "تخصصان",
            Level => "مستويان",
            _ => "عنصران"
        },
        _ => $"{Children.Count} {ChildPlural}"
    };
}
