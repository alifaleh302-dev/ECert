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

/// <summary>
/// Reader for uploaded workbooks. Owns the workbook, exposes the first sheet, and resolves
/// headers through a normalised alias table so that files produced by our own exports can be
/// fed straight back into the matching import.
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
            var key = ExcelHeaderMap.Resolve(sheet.Cell(HeaderRow, column).GetString(), aliases);
            if (key is not null) columns.TryAdd(key, column);
        }

        return new ExcelImport(workbook, sheet, columns);
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

    public string Text(int row, string key)
        => _columns.TryGetValue(key, out var column) ? Sheet.Cell(row, column).GetString().Trim() : string.Empty;

    public string? OptionalText(int row, string key)
    {
        var value = Text(row, key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
        var raw = Text(row, key);
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

        var raw = Text(row, key);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (Sheet.Cell(row, column).TryGetValue<DateTime>(out var value)) return value.Date;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out value))
            return value.Date;

        errors.Add($"التاريخ في «{label}» غير صالح.");
        return null;
    }

    public void Dispose() => _workbook.Dispose();
}
