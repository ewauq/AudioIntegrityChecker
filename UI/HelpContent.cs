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
            var sections = new System.Text.StringBuilder();
            sections.Append(locationHtml);

            sections.Append(
                $"""<div class="section-title">Technical explanation</div><p>{entry.Technical}</p>"""
            );

            if (!string.IsNullOrEmpty(entry.Simple))
                sections.Append(
                    $"""<div class="section-title">What does this mean?</div><p>{entry.Simple}</p>"""
                );

            sections.Append($"""<div class="section-title">Causes</div>{entry.Causes}""");
            sections.Append($"""<div class="section-title">How to fix</div>{entry.Fix}""");

            return WrapHtml($"<h2>{entry.Title}</h2>{sections}");
        }

        return WrapHtml(
            $"""
            <h2>{firstKey}</h2>
            {locationHtml}
            <p class="muted">No documentation available for this diagnostic.</p>
            """
        );
    }

    private record DiagnosticEntry(
        string Title,
        string Technical,
        string? Simple,
        string Causes,
        string Fix
    );

    private static readonly Dictionary<string, DiagnosticEntry> DiagnosticEntries = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["FRAME_CRC_MISMATCH"] = new(
            Title: "Frame CRC Mismatch",
            Technical: "Each audio frame can carry a CRC-16 checksum that protects the frame header "
                + "and side information (MP3) or the entire frame including audio samples (FLAC). "
                + "The checksum recomputed from the file data does not match the stored value, "
                + "proving that the protected bytes have been altered after encoding.",
            Simple: "Part of the audio data in this file has been modified or damaged since it was created. "
                + "You may hear clicks, glitches, or silence at the indicated position.",
            Causes: """
            <ul>
                <li>Bit flips on the storage medium (failing hard drive, SSD wear)</li>
                <li>Errors during file transfer (network interruption, incomplete copy)</li>
                <li>RAM corruption during read or write operations</li>
                <li>Software bug in a tool that modified the file without recalculating the checksum</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Replace the file from a backup or re-download it from the original source</li>
                <li>Re-encode from the lossless source if available</li>
                <li><b>FLAC:</b> use <i>flac -t</i> to confirm, then re-encode from the original source</li>
                <li><b>MP3:</b> use mp3val to diagnose and attempt repair of the damaged frame</li>
            </ul>
            """
        ),
        ["TRUNCATED_STREAM"] = new(
            Title: "Truncated Stream",
            Technical: "The last audio frame header declares a frame size that extends beyond the end of the file. "
                + "The frame header is valid but its payload is incomplete. "
                + "An exception is made for files ending with an ID3v1 tag, "
                + "which is not counted as truncation.",
            Simple: "The file is incomplete. The audio stops before the end of the track. "
                + "Your player may report a shorter duration or cut off the last few seconds.",
            Causes: """
            <ul>
                <li>Download interrupted before completion</li>
                <li>Disk full during encoding or file copy</li>
                <li>Power loss or application crash while the file was being written</li>
                <li>Partial file copy (e.g. removable media ejected too early)</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Re-download the file from its source</li>
                <li>Restore from a backup</li>
                <li>If the source is gone, the missing audio at the end cannot be recovered</li>
            </ul>
            """
        ),
        ["DECODE_ERROR"] = new(
            Title: "Decode Error",
            Technical: "The decoder returned an error while attempting to decompress the audio data "
                + "into PCM samples. This second-pass check runs after the structural analysis and "
                + "catches corruption that frame-level checks cannot detect, such as invalid compression "
                + "tables inside a frame.",
            Simple: "The audio data is damaged beyond what the file structure can reveal. "
                + "The decoder cannot reconstruct the original sound at the indicated position.",
            Causes: """
            <ul>
                <li>Severe corruption of the audio data within frames</li>
                <li>File is not actually an MP3 despite its extension</li>
                <li>Encoder produced non-standard output that the decoder cannot interpret</li>
                <li>Storage medium failure affecting audio data</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Replace the file from a backup or re-download it</li>
                <li>Verify the file is actually an MP3 (check with a hex editor or the <i>file</i> command)</li>
                <li>Re-encode from the original lossless source if available</li>
            </ul>
            """
        ),
        ["UNPARSEABLE_STREAM"] = new(
            Title: "Unparseable Stream",
            Technical: "The FLAC decoder cannot interpret the stream structure at all. "
                + "This goes beyond a single bad frame: "
                + "the metadata blocks or the basic stream layout "
                + "violates the FLAC specification in a fundamental way.",
            Simple: "The file is too severely damaged to be read as a FLAC file. "
                + "Most players will refuse to open it or produce no sound.",
            Causes: """
            <ul>
                <li>The file is not actually a FLAC file (wrong format, renamed extension)</li>
                <li>Severe corruption of the file header or metadata blocks</li>
                <li>File was only partially written (encoding aborted very early)</li>
                <li>A conversion tool produced invalid FLAC output</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Verify the file is actually a FLAC file</li>
                <li>Replace from a backup or re-download</li>
                <li>Re-encode from the original source if available</li>
            </ul>
            """
        ),
        ["TRAILING_GARBAGE"] = new(
            Title: "Trailing Garbage",
            Technical: "After the decoder successfully processed all audio samples declared in the stream metadata, "
                + "it found additional data that is not a valid audio frame. "
                + "The error occurs at a position equal to or beyond the total sample count.",
            Simple: "Extra non-audio data was found appended after the end of the audio stream. "
                + "The audio itself is perfectly intact. Some players may show a slightly incorrect duration.",
            Causes: """
            <ul>
                <li>A tag editor appended ID3 or APE tags after the FLAC stream</li>
                <li>A tool left padding or temporary data at the end of the file</li>
                <li>Two files were concatenated together</li>
                <li>An incomplete re-encoding left leftover bytes</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>This is usually harmless and does not require action</li>
                <li>To clean up, re-encode the file or strip trailing data with a FLAC tool</li>
            </ul>
            """
        ),
        ["LOST_SYNC"] = new(
            Title: "Lost Sync",
            Technical: "The decoder expected to find the next frame at a specific position "
                + "(computed from the previous frame's size) but found unrecognizable data instead. "
                + "For MP3, this means a significant gap before the next valid sync pattern. "
                + "For FLAC, the decoder lost frame synchronization "
                + "at a position before the declared end of the audio stream.",
            Simple: "The audio stream is broken at the indicated position. "
                + "You may hear a brief skip or a click. "
                + "In most cases the decoder recovers and the rest of the audio plays normally.",
            Causes: """
            <ul>
                <li>File corruption on the storage medium (bad sectors, failing drive)</li>
                <li>Network error during file transfer (lost or reordered packets)</li>
                <li>Power loss or crash while the file was being written</li>
                <li>A tool modified the file without maintaining frame alignment</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Re-download the file or restore from a backup</li>
                <li>Re-encode from the original lossless source if available</li>
                <li>If the skip is inaudible, the file may be acceptable as-is</li>
            </ul>
            """
        ),
        ["BAD_HEADER"] = new(
            Title: "Bad Header",
            Technical: "A frame sync pattern was found but the header fields that follow it are invalid. "
                + "For MP3: the MPEG version, layer, bitrate, or sample rate field contains "
                + "a reserved or forbidden value. "
                + "For FLAC: the frame header contains invalid field values "
                + "or its integrity check failed.",
            Simple: "A frame header is unreadable. The decoder skips this frame and continues with the next one. "
                + "You may notice a very brief gap (typically less than 30 ms) at the indicated position.",
            Causes: """
            <ul>
                <li>Corruption in the frame header area (storage or memory error)</li>
                <li>False sync detection: random data that happens to start with the sync pattern</li>
                <li>A file editing tool that did not recalculate header fields after modification</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Usually harmless since the decoder recovers automatically</li>
                <li>Re-encode the file for a perfectly clean copy</li>
                <li>Re-download if the cause is a transfer error</li>
            </ul>
            """
        ),
        ["JUNK_DATA"] = new(
            Title: "Junk Data",
            Technical: "Between two valid audio frames, a small amount of data was found that does not belong to "
                + "any recognized structure (audio frame, ID3 tag, APE tag). "
                + "Larger gaps are classified as LOST_SYNC instead.",
            Simple: "The file contains a few stray bytes between audio frames. "
                + "This has no effect on playback. Players skip over these bytes silently.",
            Causes: """
            <ul>
                <li>A tag editor left alignment padding between frames</li>
                <li>A conversion or editing tool inserted extra bytes</li>
                <li>Minor file corruption that did not damage audio data</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>No action needed. The audio is intact.</li>
                <li>To clean up, re-encode or use an MP3 repair tool to strip non-audio data</li>
            </ul>
            """
        ),
        ["XING_FRAME_COUNT_MISMATCH"] = new(
            Title: "Xing Frame Count Mismatch",
            Technical: "The Xing VBR header in the first audio frame stores a total frame count "
                + "used for seeking and duration calculation in variable bitrate files. "
                + "The stored count does not match the actual number of audio frames "
                + "(a tolerance of +/- 1 frame is allowed to account for encoder differences).",
            Simple: "The file's navigation index is wrong. The audio itself is fine, "
                + "but your player may display an incorrect duration or seek to slightly wrong positions.",
            Causes: """
            <ul>
                <li>The file was trimmed or had frames added after encoding, without updating the header</li>
                <li>The encoder calculated the frame count incorrectly</li>
                <li>An incomplete download that left the Xing header intact but removed trailing audio frames</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Use a VBR header repair tool (e.g. VBRfix or mp3val) to recalculate the correct count</li>
                <li>Re-encode to generate a fresh header</li>
            </ul>
            """
        ),
        ["INFO_FRAME_COUNT_MISMATCH"] = new(
            Title: "Info Frame Count Mismatch",
            Technical: "The Info header (CBR equivalent of the Xing header) stores a frame count "
                + "for constant bitrate files. The stored count does not match the actual number "
                + "of audio frames (tolerance of +/- 1).",
            Simple: "The file's navigation index is wrong. The audio plays correctly "
                + "but the displayed duration or seek positions may be slightly off.",
            Causes: """
            <ul>
                <li>The file was trimmed or modified after encoding without updating the Info header</li>
                <li>The encoder miscalculated the frame count</li>
                <li>Partial download that preserved the header but lost trailing frames</li>
            </ul>
            """,
            Fix: """
            <ul>
                <li>Use mp3val to recalculate the header</li>
                <li>Re-encode the file to generate a correct header</li>
            </ul>
            """
        ),
    };

    private static string WrapHtml(string body) =>
        $"<html><head><style>{Css}</style></head><body>{body}</body></html>";
}
