# Audio Integrity Checker

A Windows utility for verifying the integrity of audio files. Drop a folder, click **Start scan**, and get a clear report of every corrupt or structurally broken file.

![Screenshot](https://i.imgur.com/391WrMJ.png)

---

## How it works

Audio files can silently corrupt over time — bad sectors, interrupted transfers, incomplete downloads. Playing them may still work since media players often gloss over errors. This tool performs a full decode of every file and reports any issue found, without playing a single sample.

**FLAC** decoding is done via the official [libFLAC](https://xiph.org/flac/) library — the same library used by Audiotester and the FLAC reference encoder/decoder. Every frame's CRC is verified against the audio data.

Each file is fully loaded into RAM before decoding. This eliminates random seeks entirely, which matters especially on HDDs where seek time is the main bottleneck. **An SSD is strongly recommended** for large collections — sequential reads are an order of magnitude faster.

Worker count is auto-detected at startup: `min(CPU cores, 8)` parallel threads are used, visible in the status bar. Each thread handles one file independently with its own decoder instance.



---

## Result levels

| Level       | Meaning                                                                                                                                                                                                                                |
| ----------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **OK**      | File decoded without any issue                                                                                                                                                                                                         |
| **WARNING** | Structural anomaly detected, audio data is likely intact. Typically `LOST_SYNC` (trailing garbage after the last frame) or `BAD_HEADER` (unreadable frame header). These are common in files that were slightly mis-encoded or edited. |
| **ERROR**   | Audio data is corrupt. `FRAME_CRC_MISMATCH` means a frame's checksum doesn't match its content — samples are wrong. `UNPARSEABLE_STREAM` means the decoder could not make sense of the bitstream at all.                               |
| **SKIPPED** | Format not supported (yet)                                                                                                                                                                                                             |

---

## Supported formats

| Format     | Status    | Backend                                               |
| ---------- | --------- | ----------------------------------------------------- |
| FLAC       | Supported | libFLAC.dll (falls back to flac.exe if DLL not found) |
| MP3        | Planned   | —                                                     |
| AAC / M4A  | Planned   | —                                                     |
| WAV / AIFF | Planned   | —                                                     |
| Opus       | Planned   | —                                                     |

---

## Requirements

- Windows 10 or later (x64)
- .NET 8 Desktop Runtime
- `libFLAC.dll` placed next to the exe (or in PATH)

---

## Notes

- The app detects which backend to use automatically. If `libFLAC.dll` is not found, it falls back to `flac.exe` if available in PATH or next to the exe.
- This project was vibecoded with [Claude](https://claude.ai/claude-code) (Anthropic).

---

Suggestions and bug reports are welcome — open an issue or start a discussion.
