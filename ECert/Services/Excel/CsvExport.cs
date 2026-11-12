using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace ECert.Services.Excel;

/// <summary>
/// Builds a CSV download with the same file-naming convention as <see cref="ExcelExport"/>. The
/// UTF-8 byte-order mark is written first so Arabic text opens correctly in Excel instead of being
/// mangled by the machine's default code page.
/// </summary>
public static class CsvExport
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static FileResult Build(string namePrefix, IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows, string? scope = null)
    {
        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine(string.Join(",", headers.Select(Escape)));

        foreach (var row in rows)
            builder.AppendLine(string.Join(",", row.Select(Escape)));

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var stream = new MemoryStream(bytes);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = scope is null ? $"{namePrefix}_{stamp}.csv" : $"{namePrefix}_{scope}_{stamp}.csv";
        return new FileStreamResult(stream, ContentType) { FileDownloadName = fileName };
    }

    private static string Escape(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime date => date.ToString("yyyy-MM-dd HH:mm"),
            decimal number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
