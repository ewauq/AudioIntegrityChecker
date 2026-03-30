using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Checkers.Mp3;

internal enum Mp3Diagnostic
{
    // Pass 1 — structural
    JUNK_DATA,
    BAD_HEADER,
    FRAME_CRC_MISMATCH,
    XING_FRAME_COUNT_MISMATCH, // VBR: le header Xing indique un mauvais nombre de frames
    INFO_FRAME_COUNT_MISMATCH, // CBR: le header Info indique un mauvais nombre de frames
    LAME_TAG_CRC_MISMATCH,
    TRUNCATED_STREAM,
    LOST_SYNC,

    // Pass 2 — decode
    DECODE_ERROR,
}

internal static class Mp3DiagnosticInfo
{
    internal static bool IsError(Mp3Diagnostic d) =>
        d switch
        {
            Mp3Diagnostic.FRAME_CRC_MISMATCH => true,
            Mp3Diagnostic.TRUNCATED_STREAM => true,
            Mp3Diagnostic.DECODE_ERROR => true,
            _ => false,
        };

    internal static CheckCategory GetCategory(Mp3Diagnostic d) =>
        d switch
        {
            Mp3Diagnostic.LAME_TAG_CRC_MISMATCH => CheckCategory.Metadata,
            Mp3Diagnostic.XING_FRAME_COUNT_MISMATCH => CheckCategory.Index,
            Mp3Diagnostic.INFO_FRAME_COUNT_MISMATCH => CheckCategory.Index,
            Mp3Diagnostic.JUNK_DATA => CheckCategory.Structure,
            Mp3Diagnostic.BAD_HEADER => CheckCategory.Structure,
            Mp3Diagnostic.LOST_SYNC => CheckCategory.Structure,
            Mp3Diagnostic.FRAME_CRC_MISMATCH => CheckCategory.Corruption,
            Mp3Diagnostic.DECODE_ERROR => CheckCategory.Corruption,
            Mp3Diagnostic.TRUNCATED_STREAM => CheckCategory.Corruption,
            _ => CheckCategory.Corruption,
        };
}

[SupportedOSPlatform("windows")]
public sealed class Mp3Checker : IFormatChecker
{
    public string FormatId => "MP3";

    public CheckResult Check(
        string filePath,
        CancellationToken ct,
        IProgress<FileProgress> progress
    )
    {
        if (!File.Exists(filePath))
            return CheckResult.Error("File not found.", CheckCategory.Error);

        byte[] buf;
        try
        {
            buf = File.ReadAllBytes(filePath);
        }
        catch (OutOfMemoryException)
        {
            return CheckResult.Error("File too large to load into memory.", CheckCategory.Error);
        }
        catch (Exception ex)
        {
            return CheckResult.Error($"Cannot read file: {ex.Message}", CheckCategory.Error);
        }

        progress.Report(0.10f);
        if (ct.IsCancellationRequested)
            return CheckResult.Error("Cancelled.", CheckCategory.Error);

        // Pass 1 — structural parser (pure C#, no DLL)
        var pass1 = Mp3StructuralParser.Scan(buf);
        progress.Report(0.50f);
        if (ct.IsCancellationRequested)
            return CheckResult.Error("Cancelled.", CheckCategory.Error);

        // If Pass 1 produced any ERROR, skip Pass 2 — file is already confirmed corrupt
        if (pass1.Any(d => Mp3DiagnosticInfo.IsError(d.Diagnostic)))
            return BuildResult(pass1);

        // Pass 2 — full audio decode via mpg123 (skipped if DLL unavailable)
        List<(Mp3Diagnostic Diagnostic, long FrameIndex)> pass2 = [];
        if (Mp3Mpg123Backend.IsLibraryAvailable() && Mp3Mpg123Backend.TryInitialize())
        {
            var decodeResult = Mp3Mpg123Backend.Decode(buf);
            if (decodeResult is null)
                return CheckResult.Error(
                    "mpg123: failed to initialize decoder.",
                    CheckCategory.Error
                );
            pass2 = decodeResult;
        }

        progress.Report(1.0f);
        return BuildResult([.. pass1, .. pass2]);
    }

    private static CheckResult BuildResult(List<(Mp3Diagnostic Diagnostic, long FrameIndex)> all)
    {
        if (all.Count == 0)
            return CheckResult.Ok();

        bool hasError = all.Any(d => Mp3DiagnosticInfo.IsError(d.Diagnostic));
        string msg = string.Join(", ", all.Select(d => d.Diagnostic.ToString()).Distinct());
        long? frame = all[0].FrameIndex > 0 ? all[0].FrameIndex : null;

        // Category is the worst across all diagnostics
        var category = all.Select(d => Mp3DiagnosticInfo.GetCategory(d.Diagnostic)).Max();

        return hasError
            ? CheckResult.Error(msg, category, null, frame)
            : CheckResult.Warning(msg, category, null, frame);
    }
}
