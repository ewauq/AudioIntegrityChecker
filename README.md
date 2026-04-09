# Audio Integrity Checker

A Windows utility for verifying the integrity of audio files.

Drop a folder or individual files, click **Start scan**, and get a clear report of every corrupt or structurally broken file. A built-in help panel explains each diagnostic in plain language and suggests how to fix it.

![Screenshot](https://i.imgur.com/uBGczxN.png)

---

## How it works

Audio Integrity Checker loads each file into memory and runs it through its format-specific decoder from start to finish. There is no playback, just a full read of every byte. Any anomaly in the audio data is caught and reported.

Files are processed in parallel across multiple threads (up to 8). On an SSD this makes a noticeable difference, since the bottleneck is pure decoding speed. On a HDD, mechanical seek time between files limits throughput, so a large collection will take longer than on flash storage.

---

## FLAC

Decoding is done via the official [libFLAC](https://xiph.org/flac/) library. Every frame carries a CRC checksum that covers its audio content; the decoder verifies each one against the actual data.

If `libFLAC.dll` is not found next to the exe, the tool automatically falls back to `flac.exe` (searched in the application folder first, then in PATH).

---

## MP3

MP3 analysis runs in two sequential passes on the same in-memory buffer.

**Pass 1: Structural parser (pure C#, no external library)**

Every frame header is parsed and validated: sync word, MPEG version, Layer III marker, bitrate, and sample rate. The parser then walks the stream frame by frame, computing each frame's expected size and verifying that the next frame starts exactly where it should.

Additional checks:

- Frames that carry a CRC have their checksum verified against the frame's side information.
- The Xing header (VBR files) or Info header (CBR files), if present, is validated: the declared frame count is compared against the actual count found during the scan.
- If a LAME tag is present, its own CRC is verified.

**Pass 2: Full audio decode via [mpg123](https://www.mpg123.de/)**

The entire buffer is fed to mpg123 and fully decoded to PCM. This catches corruption that survives the structural scan: bit reservoir errors, Huffman decoding failures, and similar low-level issues.

Pass 2 is skipped if Pass 1 already found an error, since the file is already confirmed corrupt. If `mpg123.dll` is not found next to the exe, Pass 2 is disabled entirely and a warning is shown in the status bar; Pass 1 still runs.

---

## Results

Each scanned file is assigned a **Result** (OK or ISSUE) and, when an issue is found, a **Severity** that indicates how serious it is.

| Severity     | Meaning                                                                          |
| ------------ | -------------------------------------------------------------------------------- |
| **Low**      | Minor anomaly with no impact on audio playback (metadata or index inconsistency) |
| **Medium**   | Stream structure anomaly: audio data is likely intact and the file is playable  |
| **High**     | Analysis could not complete: file state is uncertain                            |
| **Critical** | Audio data is corrupted                                             |

The **Message** column gives a plain-language description of the issue along with its position in the file (timecode and frame number when available). The **Error** column contains the raw diagnostic code for reference.

Files of unsupported formats are not listed; they are silently skipped before the scan starts.

**Keyboard shortcut:** select one or more rows and press **Ctrl+C** to copy them to the clipboard as a JSON array (path, name, duration, format, result, message, error code).

---

## Diagnostics reference

### FLAC

| Diagnostic          | Severity | Technical cause                                                                                      | Common cause                                                                                      | Audio data      | Possible fix                                                                                        |
| ------------------- | -------- | ---------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- | --------------- | --------------------------------------------------------------------------------------------------- |
| `TRAILING_GARBAGE`  | Medium   | Non-audio bytes found immediately after the last valid audio frame                                   | A tagging tool appended an ID3 tag or padding to the file without cleaning up the FLAC stream     | Intact          | Strip the trailing data with a FLAC-aware tagger, or re-encode with `flac --best`                   |
| `LOST_SYNC`         | Medium   | Stream synchronisation was lost in the middle of the file                                            | File was cut, spliced, or had data inserted or removed by an editor                               | Likely intact   | Re-encode from the source; if the file was spliced, re-download the original                        |
| `BAD_HEADER`        | Medium   | Frame header that libFLAC could not parse                                                            | File was partially overwritten, or originates from a buggy encoder                                | Likely intact   | Re-encode the file; the source audio is likely fine                                                 |
| `FRAME_CRC_MISMATCH`| Critical | A frame's CRC does not match its audio content                                                       | Bad disk sector, failed transfer, or storage degradation that silently flipped bits               | **Corrupt**     | Re-download or restore from backup; the damaged samples cannot be recovered                        |
| `UNPARSEABLE_STREAM`| Critical | The bitstream is too broken for the decoder to interpret                                              | Severe corruption, wrong file extension, or an incomplete download                                | **Undecodable** | Re-download or restore from backup; try `flac --decode` to salvage any audio that is still readable |

### MP3

| Diagnostic                  | Severity | Pass | Technical cause                                                                         | Common cause                                                                                                                                  | Audio data    | Possible fix                                                                                                                          |
| --------------------------- | -------- | ---- | --------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- | ------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| `BAD_HEADER`                | Medium   | 1    | Frame header with an invalid bitrate or sample rate index                               | Produced by a buggy encoder, converter, or editing tool                                                                                       | Likely intact | Run [mp3val](https://mp3val.sourceforge.net/) to strip or repair the offending frame                                                  |
| `JUNK_DATA`                 | Medium   | 1    | 1–3 unexpected bytes found between two valid frames                                     | Left behind by a tag editor or audio editor that did not align data correctly                                                                 | Likely intact | Run mp3val to strip the gap                                                                                                           |
| `LOST_SYNC`                 | Medium   | 1    | Sync word missing at the expected position after a frame                                | File was cut, spliced, or had data inserted by an editor                                                                                      | Likely intact | Run mp3val; if the file was spliced, re-download the original                                                                         |
| `XING_FRAME_COUNT_MISMATCH` | Low      | 1    | The Xing VBR header declares a frame count that does not match the actual frame count   | File was trimmed or extended after encoding without updating the VBR header                                                                   | Intact        | Rebuild the VBR header: mp3val `-f`, or foobar2000 → right-click → *Fix VBR MP3 header*                                               |
| `INFO_FRAME_COUNT_MISMATCH` | Low      | 1    | The Info CBR header declares a frame count that does not match the actual frame count   | Buggy encoder, or an editing tool that modified the file without updating the header                                                          | Intact        | Run mp3val `-f` to rebuild the Info header                                                                                            |
| `LAME_TAG_CRC_MISMATCH`     | Low      | 1    | The LAME tag CRC does not match the first frame content                                 | The Xing/Info header was updated by a tool that did not recalculate the LAME CRC, or a buggy encoder wrote an incorrect CRC at creation time  | Intact        | No standard tool recalculates the LAME tag CRC. Audio content is unaffected — the file is safe to keep. Re-encode from a lossless source if a clean file is required. |
| `TRUNCATED_STREAM`          | Critical | 1    | End of file reached in the middle of a frame                                            | Incomplete download, interrupted transfer, or a disk write that stopped early                                                                 | **Truncated** | Re-download the file; audio up to the cut point is still playable                                                                     |
| `FRAME_CRC_MISMATCH`        | Critical | 1    | A frame's CRC does not match its side information                                       | Bit-level corruption from a bad disk sector, RAM error, or failed transfer                                                                    | **Corrupt**   | Re-download or restore from backup; the damaged frame's audio samples are wrong                                                      |
| `DECODE_ERROR`              | Critical | 2    | mpg123 reported a decode error (bit reservoir underrun, Huffman decoding failure, etc.) | Corruption that passed the structural scan but breaks actual audio decoding                                                                   | **Corrupt**   | Re-download or restore from backup; re-encoding from a lossless source is the only way to recover clean audio                         |

---

## Supported formats

| Format     | Status    | Backend                                  |
| ---------- | --------- | ---------------------------------------- |
| FLAC       | Supported | `libFLAC.dll` (falls back to `flac.exe`) |
| MP3        | Supported | Pure C# structural parser + `mpg123.dll` |
| Ogg Vorbis | Planned   | —                                        |
| AAC / M4A  | Planned   | —                                        |
| WAV / AIFF | Planned   | —                                        |
| Opus       | Planned   | —                                        |

---

## Requirements

- Windows 10 or later (x64)
- .NET 8 Desktop Runtime
- `libFLAC.dll` placed next to the exe (or in PATH)
- `mpg123.dll` placed next to the exe (optional; enables MP3 audio decode in Pass 2)

---

## Troubleshooting

**Drag and drop is blocked — cursor shows a "no entry" symbol**

This happens when the application is running with elevated privileges (administrator) while Windows Explorer is not. Windows silently blocks drag and drop from a lower-privilege process to a higher-privilege one.

Fix: launch the exe directly by double-clicking it from Explorer, or ensure your terminal is not running as administrator.

---

**MP3 files are only partially checked — status bar shows `mpg123: not found`**

Pass 2 (full audio decode) requires `mpg123.dll` to be loadable by Windows. Without it, only the structural Pass 1 runs. Download the x64 build from [mpg123.de](https://www.mpg123.de/download.shtml) and place `mpg123.dll` next to the exe.

If the DLL is present but the warning still appears, it may be built for the wrong architecture (32-bit vs 64-bit). This application requires the x64 build.

---

**FLAC files fail or show an unexpected error — status bar shows `libFLAC: not found`**

If `libFLAC.dll` is missing, the tool falls back to `flac.exe` (searched first next to the exe, then in PATH). If neither is found, FLAC analysis will fail at runtime. Ensure at least one of the two is available.

---

Suggestions and bug reports are welcome — open an issue or start a discussion.

*This project was made with [Claude](https://claude.ai/).*
