using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;

namespace ECert.Services.Excel;

/// <summary>
/// Single builder for every spreadsheet the application produces. Centralising the RTL flag,
/// the header styling, the frozen header row and the file-naming convention keeps all exports
/// visually identical and their download URLs predictable.
/// </summary>
public sealed class ExcelExport : IDisposable
{
    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string HeaderFill = "#2563eb";
    private readonly XLWorkbook _workbook = new();
    private int _row = 2;

    public ExcelExport(string sheetName, params string[] headers)
    {
        Sheet = _workbook.Worksheets.Add(sheetName);
        Sheet.RightToLeft = true;

        for (var i = 0; i < headers.Length; i++)
        {
            var cell = Sheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderFill);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        Sheet.SheetView.FreezeRows(1);
    }

    public IXLWorksheet Sheet { get; }

    /// <summary>Row number the next <see cref="WriteRow"/> call will fill.</summary>
    public int NextRow => _row;

    public void WriteRow(params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is null) continue;
            Sheet.Cell(_row, i + 1).Value = ToCellValue(values[i]!);
        }

        _row++;
    }

    /// <summary>Writes an example row and greys it out so it is not mistaken for real data.</summary>
    public ExcelExport WriteSampleRow(params object?[] values)
    {
        var target = _row;
        WriteRow(values);
        Sheet.Row(target).Style.Font.FontColor = XLColor.Gray;
        return this;
    }

    public ExcelExport ColumnWidth(int column, double width)
    {
        Sheet.Column(column).Width = width;
        return this;
    }

    public FileResult Build(string namePrefix, string? scope = null)
    {
        Sheet.Columns().AdjustToContents();

        var stream = new MemoryStream();
        _workbook.SaveAs(stream);
        stream.Position = 0;

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = scope is null
            ? $"{namePrefix}_{stamp}.xlsx"
            : $"{namePrefix}_{scope}_{stamp}.xlsx";

        return new FileStreamResult(stream, ContentType) { FileDownloadName = fileName };
    }

    public void Dispose() => _workbook.Dispose();

    private static XLCellValue ToCellValue(object value) => value switch
    {
        string text => text,
        bool flag => flag,
        DateTime date => date,
        int number => number,
        long number => number,
        double number => number,
        decimal number => number,
        _ => value.ToString() ?? string.Empty
    };
}
