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
            font-size: 13pt;
            margin: 0 0 8px 0;
        }
        .ok { color: #2e7d32; }
        .muted { color: #666; }
        .location {
            padding: 0px 4px;
            margin-top: 4px;
            font-size: 8.5pt;
            color: #444;
        }
        .section-title {
            font-weight: bold;
            margin-top: 20px;
            font-size: 11.5pt;
            color: #555;
        }
        ul { padding-left: 20px; margin: 6px 0; }
        li { margin-bottom: 4px; }
        kbd {
            border: 1px solid #ccc;
            border-radius: 3px;
            padding: 1px 4px;
            font-size: 8pt;
        }
        """;

    internal static string GetWelcomeHtml() =>
        WrapHtml(
            """
            <h2>Getting started</h2>
            <p>Add audio files to check their integrity:</p>
            <ul>
                <li><b>File &gt; Add folder...</b> to scan an entire directory</li>
                <li><b>File &gt; Add files...</b> to select individual files</li>
                <li><b>Drag and drop</b> files or folders into the window</li>
            </ul>
            <p class="muted">Then click <b>Start scan</b> to begin the analysis.</p>
            """
        );

    internal static string GetSelectFileHtml() =>
        WrapHtml(
            """
            <h2 class="muted">No file selected</h2>
            <p class="muted">Click on a file in the list to view its analysis details.</p>
            """
        );

    internal static string GetPendingHtml() =>
        WrapHtml(
            """
            <h2 class="muted">Waiting for analysis</h2>
            <p class="muted">This file has not been analyzed yet. Click <b>Start scan</b> to begin.</p>
            """
        );

    internal static string GetHtml(string? diagnosticKey, string? positionalInfo)
    {
        if (string.IsNullOrEmpty(diagnosticKey))
            return WrapHtml(
                """<h2 class="ok">No issues detected</h2><p>This file passed all integrity checks.</p>"""
            );

        var firstKey = diagnosticKey.Split(',', StringSplitOptions.TrimEntries)[0];

        var locationHtml = string.IsNullOrEmpty(positionalInfo)
            ? ""
            : $"""<div class="location">{positionalInfo}</div>""";

        if (DiagnosticEntries.TryGetValue(firstKey, out var entry))
        {
            return WrapHtml(
                $"""
                <h2>{entry.Title}</h2>
                {locationHtml}
                <div class="section-title">Technical explanation</div>
                <p>{entry.Technical}</p>
                <div class="section-title">What does this mean?</div>
                <p>{entry.Simple}</p>
                <div class="section-title">How to fix</div>
                {entry.Fix}
                """
            );
        }

        return WrapHtml(
            $"""
            <h2>{firstKey}</h2>
            {locationHtml}
            <p class="muted">No documentation available for this diagnostic.</p>
            """
        );
    }

    private record DiagnosticEntry(string Title, string Technical, string Simple, string Fix);

    private static readonly Dictionary<string, DiagnosticEntry> DiagnosticEntries = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["FRAME_CRC_MISMATCH"] = new(
            "Frame CRC Mismatch",
            "Each audio frame contains a CRC-16 checksum that protects the frame header and audio data. "
                + "The checksum computed from the actual data does not match the stored value, which means "
                + "the audio samples in this frame have been altered after encoding.",
            "Part of the audio in this file is corrupted. You may hear glitches, clicks, or silence "
                + "at the position indicated above. The damage is limited to the affected frame(s).",
            """
            <ul>
                <li>If you have a backup, replace the file with the original</li>
                <li>Re-download the file from its source</li>
                <li>Re-encode from the original lossless source if available</li>
            </ul>
            """
        ),
        ["TRUNCATED_STREAM"] = new(
            "Truncated Stream",
            "The audio stream ends abruptly before the expected number of frames. "
                + "The file size is smaller than what the frame headers indicate, suggesting "
                + "the file was cut short during transfer or encoding.",
            "The file is incomplete — the audio stops before the end of the track. "
                + "Your player may cut off the last few seconds or report an incorrect duration.",
            """
            <ul>
                <li>Re-download the file — the transfer was likely interrupted</li>
                <li>Re-encode from the original source</li>
                <li>If the truncation is minor, some players can still play what's available</li>
            </ul>
            """
        ),
        ["DECODE_ERROR"] = new(
            "Decode Error",
            "The decoder (mpg123 or libFLAC) encountered data that it could not interpret as valid audio. "
                + "This may be caused by severe corruption, an unsupported encoding feature, or "
                + "a file that is not actually in the declared format.",
            "The file cannot be played correctly. The audio data is either severely damaged or "
                + "the file is not a valid audio file despite its extension.",
            """
            <ul>
                <li>Verify the file is actually an audio file (check with a hex editor or <kbd>file</kbd> command)</li>
                <li>Re-download or re-encode from the original source</li>
                <li>If the file plays in some players, it may use a non-standard extension that this tool does not support</li>
            </ul>
            """
        ),
        ["UNPARSEABLE_STREAM"] = new(
            "Unparseable Stream",
            "The FLAC decoder could not interpret the audio stream at all. "
                + "The file structure violates the FLAC specification in a fundamental way — "
                + "this is beyond a simple frame error.",
            "The file is severely damaged and cannot be read as a FLAC file. "
                + "Most players will refuse to open it or produce no sound at all.",
            """
            <ul>
                <li>Re-download or restore from backup</li>
                <li>If this is a conversion artifact, re-encode from the original source</li>
            </ul>
            """
        ),
        ["TRAILING_GARBAGE"] = new(
            "Trailing Garbage",
            "Non-audio data was found appended after the last valid audio frame. "
                + "This is typically caused by incomplete file transfers, concatenation errors, "
                + "or software that appends metadata in a non-standard way.",
            "The audio itself is fine — the extra data at the end does not affect playback. "
                + "However, some players may report an incorrect duration or show a brief glitch at the end.",
            """
            <ul>
                <li>This is usually harmless and does not require action</li>
                <li>To clean up: re-encode the file or use a tool that can strip trailing data</li>
                <li>If the file was transferred over a network, verify the transfer completed correctly</li>
            </ul>
            """
        ),
        ["LOST_SYNC"] = new(
            "Lost Sync",
            "The decoder lost synchronization with the audio frame boundaries. "
                + "This means the expected frame sync pattern was not found where it should be, "
                + "indicating corrupted or missing data at that position in the stream.",
            "The audio stream is interrupted at the indicated position. "
                + "You may hear a brief skip or glitch. In many cases the decoder recovers "
                + "and the rest of the audio plays normally.",
            """
            <ul>
                <li>If the file plays correctly, this may be benign (minor stream anomaly)</li>
                <li>Re-download if the glitch is audible</li>
                <li>Re-encode from the original lossless source if available</li>
            </ul>
            """
        ),
        ["BAD_HEADER"] = new(
            "Bad Header",
            "A frame header contains invalid field values — for example an unsupported bitrate index, "
                + "sample rate, or layer combination. The decoder skips the malformed frame "
                + "and attempts to resynchronize with the next valid frame.",
            "One of the audio frames has a corrupted header. The audio content is likely intact "
                + "since most decoders simply skip the bad frame. You may notice a very brief skip.",
            """
            <ul>
                <li>Usually harmless — the audio is likely fine</li>
                <li>Re-encode if you want a perfectly clean file</li>
                <li>This can occur when files are edited with tools that don't properly recalculate headers</li>
            </ul>
            """
        ),
        ["JUNK_DATA"] = new(
            "Junk Data",
            "The file contains data between valid audio frames that is not recognized as audio, "
                + "metadata tags (ID3v1, ID3v2, APE), or any known ancillary structure. "
                + "This is often padding, leftover bytes from editing, or remnants of a previous encode.",
            "The file contains unexpected extra data that is not part of the audio. "
                + "Playback is not affected — players simply ignore this data.",
            """
            <ul>
                <li>This is cosmetic and does not affect audio quality</li>
                <li>To clean up: re-encode or use an MP3 tool to strip non-audio data</li>
                <li>Common after tag editors or format converters modify the file</li>
            </ul>
            """
        ),
        ["XING_FRAME_COUNT_MISMATCH"] = new(
            "Xing Frame Count Mismatch",
            "The Xing/LAME VBR header at the start of the file declares a frame count that does not match "
                + "the actual number of audio frames found in the stream. This header is used by players "
                + "for seeking and duration display in variable bitrate files.",
            "The file's navigation index is incorrect. The audio itself is fine, but your player "
                + "may show the wrong duration or seek to slightly wrong positions.",
            """
            <ul>
                <li>Use a VBR header repair tool (e.g., <kbd>VBRfix</kbd> or <kbd>mp3val</kbd>)</li>
                <li>Re-encode to regenerate a correct VBR header</li>
                <li>This does not affect audio quality — only seek accuracy and duration display</li>
            </ul>
            """
        ),
        ["INFO_FRAME_COUNT_MISMATCH"] = new(
            "Info Frame Count Mismatch",
            "The Info header (CBR equivalent of Xing) declares a frame count that does not match "
                + "the actual number of audio frames. This is the same issue as Xing mismatch "
                + "but for constant bitrate files.",
            "The file's navigation index is incorrect. The audio plays fine but the displayed "
                + "duration or seek positions may be slightly off.",
            """
            <ul>
                <li>Re-encode to regenerate a correct header</li>
                <li>Use <kbd>mp3val</kbd> to repair the header</li>
                <li>No impact on audio quality</li>
            </ul>
            """
        ),
        ["LAME_TAG_CRC_MISMATCH"] = new(
            "LAME Tag CRC Mismatch",
            "The LAME encoder tag embedded in the first frame contains a CRC that does not match "
                + "the tag data. The LAME tag stores encoder settings, replay gain values, "
                + "and gapless playback information.",
            "The encoder metadata is corrupted. The audio itself is not affected, but features "
                + "that depend on the LAME tag (like gapless playback or replay gain) may not work correctly.",
            """
            <ul>
                <li>Usually harmless for regular playback</li>
                <li>Re-encode if you need accurate replay gain or gapless playback</li>
                <li>This often happens when the file has been modified by a tag editor that overwrites the LAME tag area</li>
            </ul>
            """
        ),
    };

    private static string WrapHtml(string body) =>
        $"<html><head><style>{Css}</style></head><body>{body}</body></html>";
}
