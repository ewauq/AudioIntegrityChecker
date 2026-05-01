# Audio Integrity Checker

A Windows desktop tool that scans your audio files and tells you which ones are corrupted.

Drop a folder or a few files, hit **Start scan**, and read the results. A built-in help panel explains every issue in plain language and tells you what to do about it.

![Screenshot](https://i.imgur.com/uBGczxN.png)

## How it works

The tool reads each file from start to finish and runs it through a real audio decoder. Anything wrong with the audio (broken header, missing data, a frame that the decoder cannot read) shows up in the results.

It is **read-only**. Your files are never modified.

Multiple files are scanned in parallel. On an SSD you should see a clear speedup. On a regular hard drive the disk itself becomes the bottleneck and a large library will simply take longer.

## Reading the results

Every file gets a **Result** (OK or ISSUE) and, when something is wrong, a **Severity**:

| Severity | What it means |
|---|---|
| **Low** | Cosmetic issue. The audio plays fine. Often a metadata tag glitch. |
| **Medium** | The container has a small structural anomaly, but the audio is intact. |
| **High** | The scan could not finish. The state of the file is uncertain. |
| **Critical** | The audio itself is corrupted. You will hear it during playback. |

The **Message** column tells you what was found and where (timecode and frame number when relevant). Select rows and press **Ctrl+C** to copy them as JSON.

### Common issues you might see

- **Trailing garbage**: extra bytes left at the end of the file, often a leftover tag from another tool. Audio is fine.
- **Lost sync / Bad header**: the decoder got lost partway through the file. Usually means real corruption.
- **Frame CRC mismatch**: a checksum inside the file does not match the audio data. Real corruption.
- **Truncated stream**: the file ends in the middle of a frame. The last fraction of a second will be missing.
- **Decode error**: the decoder gave up. The audio is broken at that point.

The help panel inside the app gives you a longer explanation and a suggested fix for each one.

## Supported formats

| Format | Status |
|---|---|
| FLAC | Supported |
| MP3 | Supported |
| Ogg Vorbis | Planned |
| AAC / M4A | Planned |
| WAV / AIFF | Planned |
| Opus | Planned |

## Requirements

- Windows 10 or later (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- `libFLAC.dll` and `mpg123.dll`, both shipped with [any release](https://github.com/ewauq/AudioIntegrityChecker/releases). Drop them next to the exe, or make sure they are available in your PATH.

You can also point the app at your own copies via **Options > Libraries**.

## Troubleshooting

#### Drag and drop does not work

This happens when the app runs as administrator and Windows Explorer does not (or the other way around). Windows blocks drag and drop between different privilege levels. Launch the exe by double-clicking it from Explorer instead of from an elevated terminal.

#### MP3 files only get a partial check

The full audio decode pass needs `mpg123.dll`. Check if AIC has found the lib in File > Options > Libraries. The version shipped with the [releases](https://github.com/ewauq/AudioIntegrityChecker/releases) is the right one (64-bit). If you replaced it with your own, make sure it is the 64-bit build.

**FLAC files fail with a missing library error.** 

Same fix: `libFLAC.dll` must be next to the exe or in PATH, 64-bit.

---

Suggestions and bug reports are welcome. Open an issue or start a discussion.

*Built with [Claude](https://claude.ai/).*
