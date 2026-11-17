using System.Globalization;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.Views;

/// <summary>
/// CSV export of a view's rows (PLAN2 §11.2): UTF-8 with a byte-order mark, RFC 4180 quoting and
/// CRLF line ends, one column per table column. A cell that a spreadsheet could read as a formula
/// (it starts with =, +, -, @, a tab or a carriage return) gets a leading apostrophe, so an
/// imported title such as a HYPERLINK formula is shown as text and never run.
/// </summary>
public static class ViewExport
{
    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static string Csv(IReadOnlyList<TaskQueryRow> rows, IReadOnlyList<string> columns, IReadOnlyDictionary<string, string> tagNames)
    {
        var text = new StringBuilder();
        text.Append(string.Join(',', columns.Select(column => Quote(Header(column))))).Append("\r\n");
        foreach (var row in rows)
        {
            text.Append(string.Join(',', columns.Select(column => Quote(Neutralize(Cell(row, column, tagNames)))))).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Prefixes an apostrophe to anything a spreadsheet would evaluate as a formula.</summary>
    public static string Neutralize(string value) =>
        value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') ? "'" + value : value;

    public static string Quote(string value) =>
        value.IndexOfAny(['"', ',', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    /// <summary>A safe file name for the export: letters, digits, spaces, dashes and underscores only.</summary>
    public static string FileName(string viewName, DateTimeOffset now)
    {
        var clean = new string([.. viewName.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')]).Trim();
        if (clean.Length == 0)
        {
            clean = "view";
        }

        if (clean.Length > 60)
        {
            clean = clean[..60].TrimEnd();
        }

        return $"{clean} {now.UtcDateTime:yyyyMMdd-HHmmss}.csv";
    }

    private static string Header(string column) => column switch
    {
        "title" => "Title", "status" => "Status", "priority" => "Priority", "container" => "List",
        "planned" => "Planned", "due" => "Due", "estimate" => "Estimate (minutes)", "tags" => "Tags",
        "created" => "Created", "changed" => "Changed", "completed" => "Completed", _ => column,
    };

    private static string Cell(TaskQueryRow row, string column, IReadOnlyDictionary<string, string> tagNames)
    {
        var task = row.Task;
        return column switch
        {
            "title" => task.Title,
            "status" => task.Status.ToString(),
            "priority" => task.Priority.ToString(),
            "container" => row.ContainerName,
            "planned" => Day(task.PlannedDate, task.PlannedTime),
            "due" => Day(task.DueDate, task.DueTime),
            "estimate" => task.EstimateMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "tags" => string.Join("; ", row.TagIds.Select(id => tagNames.TryGetValue(id, out var name) ? name : id)),
            "created" => StorageFormats.Instant(task.CreatedAtUtc),
            "changed" => StorageFormats.Instant(task.UpdatedAtUtc),
            "completed" => task.CompletedAtUtc is { } completed ? StorageFormats.Instant(completed) : string.Empty,
            _ => string.Empty,
        };
    }

    private static string Day(DateOnly? date, TimeOnly? time) =>
        date is null ? string.Empty : time is null ? StorageFormats.Date(date.Value) : $"{StorageFormats.Date(date.Value)} {time.Value:HH:mm}";
}
