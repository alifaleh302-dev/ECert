using System.Globalization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;

namespace ECert.Services.Excel;

/// <summary>
/// Raised when an uploaded workbook cannot be read at all (not a spreadsheet, protected,
/// wrong shape). The message is safe to show to the user.
/// </summary>
public sealed class ExcelImportException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Why a cell could not be read as a plain value.</summary>
public enum ExcelCellIssueKind
{
    None,
    /// <summary>The cell holds an Excel error value (#REF!, #N/A, #DIV/0!, ...).</summary>
    Error,
    /// <summary>The cell is a formula whose result was never stored in the file.</summary>
    FormulaWithoutValue
}

/// <summary>
/// A problem found while reading one cell, with a message that tells the user what to do about it.
/// Reading a formula's <em>cached</em> result (never recalculating it) is what keeps a broken or
/// unsupported formula from throwing or silently producing a wrong value.
/// </summary>
public readonly record struct ExcelCellIssue(ExcelCellIssueKind Kind, string Detail)
{
    public static readonly ExcelCellIssue None = new(ExcelCellIssueKind.None, string.Empty);

    public bool HasIssue => Kind != ExcelCellIssueKind.None;

    public string Message(string label) => Kind switch
    {
        ExcelCellIssueKind.Error =>
            $"القيمة في «{label}» ناتجة عن معادلة بها خطأ {Detail} في ملف الإكسل. صحّح المعادلة في Excel أو انسخ القيم فقط، ثم أعد الرفع — أو صحّح الخلية يدوياً هنا.",
        ExcelCellIssueKind.FormulaWithoutValue =>
            $"القيمة في «{label}» معادلة لم يُحفظ لها نتيجة في الملف. افتح الملف في Excel واحفظه ليُحسب، أو صحّح الخلية يدوياً هنا.",
        _ => string.Empty
    };
}

/// <summary>
/// Reader for uploaded workbooks. Owns the workbook, exposes the first sheet, and resolves
/// headers through a normalised alias table so that files produced by our own exports can be
/// fed straight back into the matching import.
///
/// Every value is read from the cell's <em>stored</em> result (<c>CachedValue</c>) and never
/// recalculated, so a formula with an unsupported function, an error (<c>#REF!</c>) or no stored
/// result degrades into a clear message instead of an exception or a wrong number.
/// </summary>
public sealed class ExcelImport : IDisposable
{
    public const long MaxFileBytes = 10_000_000;
    public const int MaxDataRows = 5000;
    public const int HeaderRow = 1;
    public const int FirstDataRow = 2;

    private readonly XLWorkbook _workbook;
    private readonly Dictionary<string, int> _columns;

    private ExcelImport(XLWorkbook workbook, IXLWorksheet sheet, Dictionary<string, int> columns)
    {
        _workbook = workbook;
        Sheet = sheet;
        _columns = columns;
        LastRow = sheet.LastRowUsed()?.RowNumber() ?? HeaderRow;
    }

    public IXLWorksheet Sheet { get; }

    /// <summary>Number of the last row that carries data.</summary>
    public int LastRow { get; }

    /// <summary>True when any used cell in the sheet is a formula (its stored result is then used).</summary>
    public bool HasFormulas { get; private set; }

    public static ExcelImport Open(Stream stream, IReadOnlyDictionary<string, string[]> aliases)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex)
        {
            throw new ExcelImportException("تعذر قراءة الملف. تأكد أنه ملف XLSX سليم وغير محمي بكلمة مرور.", ex);
        }

        var sheet = workbook.Worksheets.FirstOrDefault();
        var used = sheet?.RangeUsed();
        if (sheet is null || used is null || used.RowCount() < 2)
        {
            workbook.Dispose();
            throw new ExcelImportException("الملف فارغ أو لا يحتوي على صفوف بيانات.");
        }

        if (used.RowCount() - 1 > MaxDataRows)
        {
            workbook.Dispose();
            throw new ExcelImportException($"عدد الصفوف يتجاوز الحد المسموح ({MaxDataRows} صف).");
        }

        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 1; column <= used.ColumnCount(); column++)
        {
            var key = ExcelHeaderMap.Resolve(ReadCellText(sheet.Cell(HeaderRow, column)), aliases);
            if (key is not null) columns.TryAdd(key, column);
        }

        var import = new ExcelImport(workbook, sheet, columns);
        import.HasFormulas = used.CellsUsed(cell => cell.HasFormula).Any();
        return import;
    }

    /// <summary>Validates the extension and size of an uploaded file, returning an error message when unusable.</summary>
    public static string? ValidateUpload(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return "اختر ملف XLSX صالحاً.";

        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return "يسمح فقط بملفات XLSX.";

        if (file.Length > MaxFileBytes)
            return $"حجم الملف يتجاوز الحد المسموح ({MaxFileBytes / 1_000_000} ميجابايت).";

        return null;
    }

    public bool HasColumn(string key) => _columns.ContainsKey(key);

    public IReadOnlyList<string> MissingColumns(IEnumerable<string> required)
        => required.Where(key => !_columns.ContainsKey(key)).ToList();

    /// <summary>Reads the stored value of a mapped cell as text, with no recalc and no exception.</summary>
    public string Text(int row, string key)
        => _columns.TryGetValue(key, out var column) ? ReadCellText(Sheet.Cell(row, column)) : string.Empty;

    public string? OptionalText(int row, string key)
    {
        var value = Text(row, key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Reads a mapped cell and reports whether it held an Excel error or an uncalculated formula.</summary>
    public (string Text, ExcelCellIssue Issue) Cell(int row, string key, string label)
    {
        if (!_columns.TryGetValue(key, out var column)) return (string.Empty, ExcelCellIssue.None);
        return ReadCell(Sheet.Cell(row, column));
    }

    public bool Flag(int row, string key)
    {
        var value = OptionalText(row, key);
        return value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("نعم", StringComparison.OrdinalIgnoreCase)
            || value == "1");
    }

    public decimal Decimal(int row, string key, string label, List<string> errors)
    {
        var (raw, issue) = Cell(row, key, label);
        if (issue.HasIssue)
        {
            errors.Add(issue.Message(label));
            return 0;
        }

        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out value))
            return value;

        errors.Add($"القيمة الرقمية في «{label}» غير صالحة.");
        return 0;
    }

    public DateTime? Date(int row, string key, string label, List<string> errors)
    {
        if (!_columns.TryGetValue(key, out var column)) return null;

        var cell = Sheet.Cell(row, column);
        var cached = cell.CachedValue;

        if (cached.IsError)
        {
            errors.Add(ErrorIssue(cached).Message(label));
            return null;
        }

        if (cached.IsDateTime) return cached.GetDateTime().Date;

        var (raw, issue) = ReadCell(cell);
        if (issue.HasIssue)
        {
            errors.Add(issue.Message(label));
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out value))
            return value.Date;

        errors.Add($"التاريخ في «{label}» غير صالح.");
        return null;
    }

    public void Dispose() => _workbook.Dispose();

    /// <summary>
    /// Reads a cell's stored result. Text is returned as-is, numbers are formatted without a
    /// trailing <c>.0</c>, dates use an ISO format, and errors are surfaced as an issue.
    /// </summary>
    private static (string Text, ExcelCellIssue Issue) ReadCell(IXLCell cell)
    {
        var value = cell.CachedValue;

        if (value.IsError)
            return (string.Empty, ErrorIssue(value));

        if (value.IsBlank)
            return cell.HasFormula
                ? (string.Empty, new ExcelCellIssue(ExcelCellIssueKind.FormulaWithoutValue, string.Empty))
                : (string.Empty, ExcelCellIssue.None);

        if (value.IsDateTime)
            return (value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ExcelCellIssue.None);

        if (value.IsNumber)
        {
            var number = value.GetNumber();
            return (number == Math.Floor(number) && Math.Abs(number) < 1e15
                ? ((long)number).ToString(CultureInfo.InvariantCulture)
                : number.ToString(CultureInfo.InvariantCulture), ExcelCellIssue.None);
        }

        if (value.IsBoolean)
            return (value.GetBoolean() ? "TRUE" : "FALSE", ExcelCellIssue.None);

        if (value.IsTimeSpan)
            return (value.GetTimeSpan().ToString(), ExcelCellIssue.None);

        return (value.GetText().Trim(), ExcelCellIssue.None);
    }

    private static string ReadCellText(IXLCell cell)
    {
        var value = cell.CachedValue;
        if (value.IsError || value.IsBlank) return string.Empty;
        return ReadCell(cell).Text;
    }

    private static ExcelCellIssue ErrorIssue(XLCellValue value)
        => new(ExcelCellIssueKind.Error, DescribeError(value.GetError()));

    private static string DescribeError(XLError error) => error switch
    {
        XLError.CellReference => "#REF!",
        XLError.NoValueAvailable => "#N/A",
        XLError.DivisionByZero => "#DIV/0!",
        XLError.NameNotRecognized => "#NAME?",
        XLError.NumberInvalid => "#NUM!",
        XLError.NullValue => "#NULL!",
        XLError.IncompatibleValue => "#VALUE!",
        _ => error.ToString()
    };
}
