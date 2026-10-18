namespace ECert.Services.Excel;

/// <summary>
/// Canonical column keys for every import, together with the header spellings accepted for each.
/// The Arabic spellings are the ones our own exports write, which is what makes an exported file
/// re-importable without editing its header row.
/// </summary>
public static class ExcelColumns
{
    public const string CourseName = "coursename";
    public const string CourseNameEnglish = "coursenameenglish";
    public const string Category = "category";
    public const string Instructors = "instructors";
    public const string Price = "price";
    public const string DiscountType = "discounttype";
    public const string DiscountValue = "discountvalue";
    public const string StartDate = "startdate";
    public const string EndDate = "enddate";
    public const string Location = "location";
    public const string Status = "status";
    public const string IsFeatured = "isfeatured";
    public const string RequiresAcademicDetails = "requiresacademicdetails";
    public const string ShortDescription = "shortdescription";
    public const string FullDescription = "fulldescription";
    public const string Objectives = "objectives";
    public const string Content = "content";

    public const string FullNameArabic = "fullnamearabic";
    public const string FullNameEnglish = "fullnameenglish";
    public const string Gender = "gender";
    public const string Phone = "phone";
    public const string Country = "country";
    public const string Email = "email";
    public const string HeardFrom = "heardfrom";
    public const string University = "university";
    public const string College = "college";
    public const string Specialization = "specialization";
    public const string AcademicLevel = "academiclevel";

    public static readonly IReadOnlyDictionary<string, string[]> CourseAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [CourseName] = ["CourseName", "اسم الدورة", "اسم الدوره", "الدورة", "الدوره", "اسم الدورة (عربي)"],
            [CourseNameEnglish] = ["CourseNameEnglish", "اسم الدورة (إنجليزي)", "اسم الدورة (انجليزي)", "اسم الدورة بالإنجليزية"],
            [Category] = ["Category", "الفئة", "الفئه"],
            [Instructors] = ["Instructors", "المدرب", "المدربون", "اسم المدرب", "اسماء المدربين", "أسماء المدربين"],
            [Price] = ["Price", "السعر", "سعر الدورة", "سعر الدوره"],
            [DiscountType] = ["DiscountType", "نوع الخصم"],
            [DiscountValue] = ["DiscountValue", "قيمة الخصم"],
            [StartDate] = ["StartDate", "تاريخ البداية", "تاريخ البدء"],
            [EndDate] = ["EndDate", "تاريخ النهاية", "تاريخ الانتهاء"],
            [Location] = ["Location", "المكان"],
            [Status] = ["Status", "الحالة"],
            [IsFeatured] = ["IsFeatured", "مميزة", "دورة مميزة", "دوره مميزه"],
            [RequiresAcademicDetails] = ["RequiresAcademicDetails", "تتطلب بيانات أكاديمية", "تتطلب بيانات اكاديمية"],
            [ShortDescription] = ["ShortDescription", "الوصف المختصر"],
            [FullDescription] = ["FullDescription", "الوصف الكامل"],
            [Objectives] = ["Objectives", "أهداف الدورة", "اهداف الدورة", "أهداف الدوره", "اهداف الدوره", "الأهداف", "الاهداف"],
            [Content] = ["Content", "محتوى الدورة", "محتوى الدوره", "المحتوى"]
        };

    public static readonly IReadOnlyDictionary<string, string[]> RegistrationAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [FullNameArabic] = ["FullNameArabic", "الاسم بالعربية", "الاسم العربي", "اسم المتدرب بالعربية", "الاسم الاول", "الاسم"],
            [FullNameEnglish] = ["FullNameEnglish", "الاسم بالإنجليزية", "الاسم الانجليزي", "الاسم بالانجليزية", "اسم المتدرب بالانجليزية"],
            [Gender] = ["Gender", "الجنس"],
            [Phone] = ["Phone", "الهاتف", "رقم الهاتف", "الجوال", "رقم الجوال"],
            [Country] = ["Country", "الدولة", "مفتاح الدولة", "الدوله"],
            [Email] = ["Email", "البريد الإلكتروني", "البريد الالكتروني", "الايميل"],
            [HeardFrom] = ["HeardFrom", "كيف سمعت عنا"],
            [University] = ["University", "الجامعة", "الجامعه"],
            [College] = ["College", "الكلية", "الكليه"],
            [Specialization] = ["Specialization", "التخصص"],
            [AcademicLevel] = ["AcademicLevel", "المستوى الدراسي", "المستوي الدراسي"]
        };
}
