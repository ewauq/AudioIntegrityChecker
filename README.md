# Audio Integrity Checker

A Windows utility for verifying the integrity of audio files.

Just drop a folder or files, click **Start scan**, and get a clear report of every corrupt or structurally broken file.

![Screenshot](https://i.imgur.com/Wnxap6o.png)

---

## How it works

Audio Integrity Checker loads each file entirely into memory and runs it through its format-specific decoder from start to finish. There is no playback — just a full read of every byte. If anything is wrong with the audio data, it gets caught and reported.

Files are processed in parallel using multithreading. On an SSD this makes a substantial difference, since the bottleneck shifts to pure decoding speed. On a HDD, random access between files is slow by nature — sequential reads help, but a large collection will still take longer than on flash storage.

---

## FLAC

Decoding is done via the official [libFLAC](https://xiph.org/flac/) library — the same library used by the FLAC reference encoder/decoder. Every frame's CRC is verified against the audio data.

If `libFLAC.dll` is not found next to the exe, the tool automatically falls back to `flac.exe` (looked up in PATH or next to the exe).

---

## MP3

Analysis runs in two sequential passes on the same in-memory buffer.

**Pass 1 — Structural parser (native)**

Every frame header is parsed and validated: sync word, MPEG version, Layer III marker, bitrate index, sample rate index. Frame sizes are computed and the parser walks the stream frame by frame, checking that each one starts exactly where expected.

- CRC-protected frames (protection bit = 0) have their CRC-16 verified against the side information bytes.
- The Xing header (VBR) or Info header (CBR), if present, is checked: the declared frame count is compared against the actual count found during the scan.
- If a LAME tag is present, its CRC-16 (covering the first 190 bytes of the first frame) is verified.

**Pass 2 — Full audio decode ([mpg123](https://www.mpg123.de/))**

The entire buffer is fed to mpg123 and decoded to PCM. This catches bit reservoir errors, Huffman decoding failures, and any corruption that survives the structural scan.

If `mpg123.dll` is not found, Pass 2 is disabled and a warning is shown — Pass 1 still runs.

---

## Result levels

| Level | Meaning |
|---|---|
| **OK** | File passed all checks without issue |
| **WARNING** | Structural anomaly detected — audio data is likely intact and playable |
| **ERROR** | Audio data is corrupt or the stream is undecodable |
| **SKIPPED** | Format not supported |

---

## Diagnostics reference

### FLAC

| Diagnostic | Level | Triggered by | Audio data | Possible fix |
|---|---|---|---|---|
| `LOST_SYNC` | WARNING | Garbage bytes after the last valid frame — common in slightly mis-encoded or edited files | Likely intact | Re-encode the file with `flac --best` to produce a clean stream |
| `BAD_HEADER` | WARNING | Frame header that libFLAC could not parse | Likely intact | Re-encode the file; the source audio is likely fine |
| `FRAME_CRC_MISMATCH` | ERROR | A frame's CRC doesn't match its audio content — the samples stored on disk are wrong | **Corrupt** | Re-download or restore from backup — the affected samples cannot be recovered |
| `UNPARSEABLE_STREAM` | ERROR | The bitstream structure is so broken the decoder cannot make sense of it | **Undecodable** | Re-download or restore from backup; try `flac --decode` to salvage whatever audio is still readable |

### MP3

| Diagnostic | Level | Pass | Triggered by | Audio data | Possible fix |
|---|---|---|---|---|---|
| `BAD_HEADER` | WARNING | 1 | Frame header with an invalid bitrate or sample rate index — parser skipped the frame | Likely intact | Run [mp3val](https://mp3val.sourceforge.net/) to strip or repair the offending frame |
| `JUNK_DATA` | WARNING | 1 | 1–3 unexpected bytes between two otherwise valid frames (small alignment gap) | Likely intact | Run mp3val to strip the gap; usually left by editors or taggers |
| `LOST_SYNC` | WARNING | 1 | Sync word missing at the expected position after a frame — larger gap, possibly cut or spliced audio | Likely intact | Run mp3val; if the file was spliced, re-download the original |
| `XING_FRAME_COUNT_MISMATCH` | WARNING | 1 | The Xing VBR header declares a different frame count than what the parser actually counted — seek table may be off | Likely intact | Rebuild the VBR header: mp3val `-f`, or foobar2000 → right-click → *Fix VBR MP3 header* |
| `INFO_FRAME_COUNT_MISMATCH` | WARNING | 1 | The Info CBR header declares a different frame count than what the parser actually counted — header was likely written by a buggy encoder or editor | Likely intact | Run mp3val `-f` to rebuild the Info header |
| `LAME_TAG_CRC_MISMATCH` | WARNING | 1 | The LAME tag embedded in the first frame has an invalid CRC — encoder metadata is unreliable | Likely intact | Rebuild the LAME tag with mp3val; does not affect audio content |
| `TRUNCATED_STREAM` | ERROR | 1 | End of file reached mid-frame — the file was cut short | **Truncated** | Re-download; the file is incomplete. Partial audio up to the cut is playable |
| `FRAME_CRC_MISMATCH` | ERROR | 1 | A CRC-protected frame's checksum doesn't match its side information — frame header or side data is wrong | **Corrupt** | Re-download or restore from backup — the affected frame's samples are wrong |
| `DECODE_ERROR` | ERROR | 2 | mpg123 returned a decode error — covers bit reservoir underruns, Huffman decoding failures, and other low-level issues | **Corrupt** | Re-download or restore from backup; re-encoding from a lossless source is the only way to recover |
| `INIT_FAILED` | ERROR | 2 | mpg123 handle could not be created or opened — internal library error | — | Verify that `mpg123.dll` is not corrupted; re-download it from [mpg123.de](https://www.mpg123.de/download.shtml) |

---

## Supported formats

| Format | Status | Backend |
|---|---|---|
| FLAC | Supported | `libFLAC.dll` (falls back to `flac.exe`) |
| MP3 | Supported | Structural parser + `mpg123.dll` |
| AAC / M4A | Planned | — |
| WAV / AIFF | Planned | — |
| Opus | Planned | — |

---

## Requirements

- Windows 10 or later (x64)
- .NET 8 Desktop Runtime
- `libFLAC.dll` placed next to the exe (or in PATH)
- `mpg123.dll` placed next to the exe — optional, enables MP3 audio decode (Pass 2)

---

Suggestions and bug reports are welcome — open an issue or start a discussion.

*This project was made with [Claude](https://claude.ai/).*
