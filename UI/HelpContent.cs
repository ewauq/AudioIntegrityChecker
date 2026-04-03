using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal static class HelpContent
{
    private const string Css = """
        body {
            font-family: Segoe UI, sans-serif;
            font-size: 9pt;
            margin: 8px;
            color: #222;
        }
        h2 {
            font-size: 11pt;
            margin: 0 0 8px 0;
            padding-bottom: 4px;
            border-bottom: 1px solid #ccc;
        }
        .ok { color: #2e7d32; }
        .section-title {
            font-weight: bold;
            margin-top: 12px;
            margin-bottom: 4px;
            color: #555;
        }
        .placeholder {
            color: #999;
            font-style: italic;
        }
        """;

    private static readonly Dictionary<string, string> DiagnosticTitles = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // MP3 diagnostics
        ["JUNK_DATA"] = "Junk Data",
        ["BAD_HEADER"] = "Bad Header",
        ["FRAME_CRC_MISMATCH"] = "Frame CRC Mismatch",
        ["XING_FRAME_COUNT_MISMATCH"] = "Xing Frame Count Mismatch",
        ["INFO_FRAME_COUNT_MISMATCH"] = "Info Frame Count Mismatch",
        ["LAME_TAG_CRC_MISMATCH"] = "LAME Tag CRC Mismatch",
        ["TRUNCATED_STREAM"] = "Truncated Stream",
        ["LOST_SYNC"] = "Lost Sync",
        ["DECODE_ERROR"] = "Decode Error",

        // FLAC diagnostics
        ["UNPARSEABLE_STREAM"] = "Unparseable Stream",
        ["TRAILING_GARBAGE"] = "Trailing Garbage",
    };

    internal static string GetHtml(string? diagnosticKey)
    {
        if (string.IsNullOrEmpty(diagnosticKey))
            return WrapHtml(
                """<h2 class="ok">No issues detected</h2><p>This file passed all integrity checks.</p>"""
            );

        // The error column may contain multiple diagnostics comma-separated; take the first one
        var firstKey = diagnosticKey.Split(',', StringSplitOptions.TrimEntries)[0];

        var title = DiagnosticTitles.TryGetValue(firstKey, out var t) ? t : firstKey;

        return WrapHtml(
            $"""
            <h2>{title}</h2>
            <div class="section-title">Technical explanation</div>
            <p class="placeholder">Documentation coming soon.</p>
            <div class="section-title">What does this mean?</div>
            <p class="placeholder">Documentation coming soon.</p>
            <div class="section-title">How to fix</div>
            <p class="placeholder">Documentation coming soon.</p>
            """
        );
    }

    private static string WrapHtml(string body) =>
        $"<html><head><style>{Css}</style></head><body>{body}</body></html>";
}
