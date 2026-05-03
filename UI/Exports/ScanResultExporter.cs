using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.UI.Exports;

internal enum ExportFormat
{
    Text,
    Csv,
    Html,
}

internal enum ExportScope
{
    IssuesOnly,
    AllFiles,
}

internal sealed record ScanSummary(
    int TotalFiles,
    TimeSpan Elapsed,
    int Metadata,
    int Index,
    int Structure,
    int Corruption,
    int Error,
    DateTime GeneratedAt
)
{
    internal int IssuesCount => Metadata + Index + Structure + Corruption + Error;
}

[SupportedOSPlatform("windows")]
internal static class ScanResultExporter
{
    internal static string SuggestFileName(ExportFormat format)
    {
        string ext = format switch
        {
            ExportFormat.Text => "txt",
            ExportFormat.Csv => "csv",
            ExportFormat.Html => "html",
            _ => "txt",
        };
        string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
        return $"audio-integrity-report-{stamp}.{ext}";
    }

    internal static string FilterFor(ExportFormat format) =>
        format switch
        {
            ExportFormat.Text => "Text file (*.txt)|*.txt",
            ExportFormat.Csv => "CSV file (*.csv)|*.csv",
            ExportFormat.Html => "HTML file (*.html)|*.html",
            _ => "All files (*.*)|*.*",
        };

    internal static void Write(
        ExportFormat format,
        IReadOnlyList<CompletedFileSnapshot> rows,
        ScanSummary summary,
        Stream output
    )
    {
        switch (format)
        {
            case ExportFormat.Text:
                WriteText(rows, summary, output);
                break;
            case ExportFormat.Csv:
                WriteCsv(rows, summary, output);
                break;
            case ExportFormat.Html:
                WriteHtml(rows, summary, output);
                break;
        }
    }

    private static void WriteText(
        IReadOnlyList<CompletedFileSnapshot> rows,
        ScanSummary summary,
        Stream output
    )
    {
        using var w = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        )
        {
            NewLine = "\r\n",
        };

        w.WriteLine("Audio Integrity Checker - scan report");
        w.WriteLine($"Generated: {summary.GeneratedAt:yyyy-MM-dd HH:mm}");
        w.WriteLine();
        w.WriteLine(
            $"Checked {summary.TotalFiles} file{(summary.TotalFiles == 1 ? "" : "s")} in {FormatElapsed(summary.Elapsed)}."
        );
        string breakdown = FormatBreakdown(summary);
        if (breakdown.Length > 0)
            w.WriteLine(breakdown);
        w.WriteLine();

        const int wSev = 9;
        const int wFile = 60;
        const int wFmt = 6;
        const int wDur = 11;

        w.WriteLine(
            $"{"SEV".PadRight(wSev)}  {"FILE".PadRight(wFile)}  {"FMT".PadRight(wFmt)}  {"DURATION".PadRight(wDur)}  ISSUE"
        );
        w.WriteLine(new string('-', wSev + 2 + wFile + 2 + wFmt + 2 + wDur + 2 + 40));

        foreach (var row in rows)
        {
            string sev = FormatSeverity(row.Result).PadRight(wSev);
            string file = TruncateOrPad(row.Path, wFile);
            string fmt = (row.Format ?? "").PadRight(wFmt);
            string dur = FormatDurationLong(row.Duration).PadRight(wDur);
            string msg =
                row.Result.Category == CheckCategory.Ok
                    ? "OK"
                    : ResultFormatting.BuildMessageColumnText(row.Result);
            w.WriteLine($"{sev}  {file}  {fmt}  {dur}  {msg}");
        }
    }

    private static void WriteCsv(
        IReadOnlyList<CompletedFileSnapshot> rows,
        ScanSummary summary,
        Stream output
    )
    {
        using var w = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        )
        {
            NewLine = "\r\n",
        };

        w.WriteLine("Severity,File,Format,Duration,Result,Message,ErrorCode");

        foreach (var row in rows)
        {
            string sev = FormatSeverity(row.Result);
            string file = CsvEscape(row.Path);
            string fmt = CsvEscape(row.Format ?? "");
            string dur = CsvEscape(FormatDurationLong(row.Duration));
            string result = row.Result.Category == CheckCategory.Ok ? "OK" : "ISSUE";
            string msg = CsvEscape(
                row.Result.Category == CheckCategory.Ok
                    ? ""
                    : ResultFormatting.BuildMessageColumnText(row.Result)
            );
            string code = CsvEscape(row.Result.ErrorMessage ?? "");
            w.WriteLine($"{sev},{file},{fmt},{dur},{result},{msg},{code}");
        }

        // Trailing summary line as a comment-like row that Excel still parses
        // cleanly into a single column.
        w.WriteLine();
        w.WriteLine(
            CsvEscape(
                $"Checked {summary.TotalFiles} files in {FormatElapsed(summary.Elapsed)}. "
                    + $"Generated {summary.GeneratedAt:yyyy-MM-dd HH:mm}."
            )
        );
    }

    private static void WriteHtml(
        IReadOnlyList<CompletedFileSnapshot> rows,
        ScanSummary summary,
        Stream output
    )
    {
        using var w = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
        )
        {
            NewLine = "\r\n",
        };

        w.WriteLine("<!DOCTYPE html>");
        w.WriteLine("<html lang=\"en\">");
        w.WriteLine("<head>");
        w.WriteLine("<meta charset=\"utf-8\">");
        w.WriteLine("<title>Audio Integrity Checker - scan report</title>");
        w.WriteLine("<style>");
        w.WriteLine(
            "body{font:14px -apple-system,'Segoe UI',sans-serif;padding:24px;color:#222;background:#fff;}"
        );
        w.WriteLine("h1{font-size:18px;margin:0 0 4px;}");
        w.WriteLine(".summary{color:#666;margin-bottom:24px;}");
        w.WriteLine("table{border-collapse:collapse;width:100%;}");
        w.WriteLine(
            "th{background:#f4f4f4;text-align:left;padding:8px 12px;position:sticky;top:0;border-bottom:1px solid #ddd;font-weight:600;}"
        );
        w.WriteLine("td{padding:6px 12px;border-bottom:1px solid #eee;vertical-align:top;}");
        w.WriteLine(".sev{font-weight:600;white-space:nowrap;}");
        w.WriteLine(".sev-critical{color:#dc143c;}");
        w.WriteLine(".sev-high{color:#ff4500;}");
        w.WriteLine(".sev-medium{color:#daa520;}");
        w.WriteLine(".sev-low{color:#555;}");
        w.WriteLine(".sev-none{color:#2e7d32;}");
        w.WriteLine("td.path{font-family:Consolas,'Courier New',monospace;font-size:13px;}");
        w.WriteLine("</style>");
        w.WriteLine("</head>");
        w.WriteLine("<body>");
        w.WriteLine("<h1>Audio Integrity Checker - scan report</h1>");

        string breakdown = FormatBreakdown(summary);
        string genStamp = summary.GeneratedAt.ToString(
            "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture
        );
        string summaryLine =
            $"Checked {summary.TotalFiles} file{(summary.TotalFiles == 1 ? "" : "s")} in {FormatElapsed(summary.Elapsed)}. Generated {genStamp}.";
        w.WriteLine($"<div class=\"summary\">{HtmlEncode(summaryLine)}");
        if (breakdown.Length > 0)
            w.WriteLine($"<br>{HtmlEncode(breakdown)}");
        w.WriteLine("</div>");

        w.WriteLine("<table>");
        w.WriteLine(
            "<thead><tr><th>Severity</th><th>File</th><th>Format</th><th>Duration</th><th>Result</th><th>Issue</th><th>Code</th></tr></thead>"
        );
        w.WriteLine("<tbody>");

        foreach (var row in rows)
        {
            var severity = ResultFormatting.GetSeverity(row.Result.Category);
            string sevClass = severity switch
            {
                ResultSeverity.Critical => "sev-critical",
                ResultSeverity.High => "sev-high",
                ResultSeverity.Medium => "sev-medium",
                ResultSeverity.Low => "sev-low",
                _ => "sev-none",
            };
            string sevText = FormatSeverity(row.Result);
            string fmt = HtmlEncode(row.Format ?? "");
            string dur = HtmlEncode(FormatDurationLong(row.Duration));
            string path = HtmlEncode(row.Path);
            string result = row.Result.Category == CheckCategory.Ok ? "OK" : "ISSUE";
            string msg = HtmlEncode(
                row.Result.Category == CheckCategory.Ok
                    ? ""
                    : ResultFormatting.BuildMessageColumnText(row.Result)
            );
            string code = HtmlEncode(row.Result.ErrorMessage ?? "");

            w.WriteLine(
                $"<tr><td class=\"sev {sevClass}\">{sevText}</td><td class=\"path\">{path}</td><td>{fmt}</td><td>{dur}</td><td>{result}</td><td>{msg}</td><td>{code}</td></tr>"
            );
        }

        w.WriteLine("</tbody>");
        w.WriteLine("</table>");
        w.WriteLine("</body>");
        w.WriteLine("</html>");
    }

    // ---------------------------------------------------------------------
    // Formatting helpers
    // ---------------------------------------------------------------------

    private static string FormatSeverity(CheckResult result) =>
        result.Category == CheckCategory.Ok
            ? "OK"
            : ResultFormatting.GetSeverity(result.Category) switch
            {
                ResultSeverity.Critical => "CRITICAL",
                ResultSeverity.High => "HIGH",
                ResultSeverity.Medium => "MEDIUM",
                ResultSeverity.Low => "LOW",
                _ => "",
            };

    private static string FormatBreakdown(ScanSummary s)
    {
        var parts = new List<string>(5);
        if (s.Corruption > 0)
            parts.Add($"{s.Corruption} corruption");
        if (s.Error > 0)
            parts.Add($"{s.Error} error{(s.Error == 1 ? "" : "s")}");
        if (s.Structure > 0)
            parts.Add($"{s.Structure} structure");
        if (s.Index > 0)
            parts.Add($"{s.Index} index");
        if (s.Metadata > 0)
            parts.Add($"{s.Metadata} metadata");
        return string.Join(" · ", parts);
    }

    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
            return $"{(int)elapsed.TotalMilliseconds} ms";
        if (elapsed.TotalSeconds < 60)
            return $"{elapsed.TotalSeconds:F1} s";
        if (elapsed.TotalHours < 1)
            return $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds:D2} s";
        return $"{(int)elapsed.TotalHours} h {elapsed.Minutes:D2} min {elapsed.Seconds:D2} s";
    }

    private static string FormatDurationLong(TimeSpan? duration)
    {
        if (duration is null)
            return "";
        var d = duration.Value;
        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}.{d.Milliseconds:D3}"
            : $"{d.Minutes:D2}:{d.Seconds:D2}.{d.Milliseconds:D3}";
    }

    private static string TruncateOrPad(string s, int width)
    {
        if (s.Length <= width)
            return s.PadRight(width);
        // Keep the tail of the path which carries the file name, ellipsize the head.
        return "..." + s[^(width - 3)..];
    }

    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny(['"', ',', '\r', '\n']) < 0)
            return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static string HtmlEncode(string s) => WebUtility.HtmlEncode(s);
}
