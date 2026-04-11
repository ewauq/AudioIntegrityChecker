using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;
using AudioIntegrityChecker.Pipeline;

namespace AudioIntegrityChecker.Checkers.Mp3;

internal enum Mp3Diagnostic
{
    // Pass 1: structural
    JUNK_DATA,
    BAD_HEADER,
    FRAME_CRC_MISMATCH,
    XING_FRAME_COUNT_MISMATCH, // VBR: Xing header reports an incorrect frame count
    INFO_FRAME_COUNT_MISMATCH, // CBR: Info header reports an incorrect frame count
    LAME_TAG_CRC_MISMATCH,
    TRUNCATED_STREAM,
    LOST_SYNC,

    // Pass 2: decode
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
internal sealed class Mp3Checker : IFormatChecker, IBufferedChecker
{
    public string FormatId => "MP3";

    public CheckOutcome Check(
        string filePath,
        CancellationToken ct,
        IProgress<FileProgress> progress
    )
    {
        if (!File.Exists(filePath))
            return new CheckOutcome(
                CheckResult.Error("File not found.", CheckCategory.Error),
                null
            );

        FileBuffer buffer;
        try
        {
            buffer = FileBuffer.Load(filePath);
        }
        catch (OutOfMemoryException)
        {
            return new CheckOutcome(
                CheckResult.Error("File too large to load into memory.", CheckCategory.Error),
                null
            );
        }
        catch (Exception ex)
        {
            return new CheckOutcome(
                CheckResult.Error($"Cannot read file: {ex.Message}", CheckCategory.Error),
                null
            );
        }

        using (buffer)
            return DecodeBuffer(buffer.AsArray(), ct, progress);
    }

    CheckOutcome IBufferedChecker.CheckWithBuffer(
        string filePath,
        FileBuffer buffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    ) => DecodeBuffer(buffer.AsArray(), cancellationToken, progress);

    private static CheckOutcome DecodeBuffer(
        byte[] buf,
        CancellationToken ct,
        IProgress<FileProgress> progress
    )
    {
        // Extract duration directly from the in-memory buffer, avoids reopening the
        // file during the scan phase. Computed up front so it is returned even when
        // a later check step errors out.
        TimeSpan? duration = Mp3MetadataReader.TryReadDuration(buf);

        progress.Report(0.10f);
        if (ct.IsCancellationRequested)
            return new CheckOutcome(CheckResult.Error("Cancelled.", CheckCategory.Error), duration);

        // Pass 1: structural parser (pure C#, no DLL)
        var pass1 = Mp3StructuralParser.Scan(buf);
        progress.Report(0.50f);
        if (ct.IsCancellationRequested)
            return new CheckOutcome(CheckResult.Error("Cancelled.", CheckCategory.Error), duration);

        // If Pass 1 produced any ERROR, skip Pass 2: file is already confirmed corrupt
        if (pass1.Any(d => Mp3DiagnosticInfo.IsError(d.Diagnostic)))
            return new CheckOutcome(BuildResult(pass1), duration);

        // Pass 2: full audio decode via mpg123 (skipped if DLL unavailable)
        List<(Mp3Diagnostic Diagnostic, long FrameIndex)> pass2 = [];
        if (Mp3Mpg123Backend.IsLibraryAvailable() && Mp3Mpg123Backend.TryInitialize())
        {
            var decodeResult = Mp3Mpg123Backend.Decode(buf);
            if (decodeResult is null)
                return new CheckOutcome(
                    CheckResult.Error("mpg123: failed to initialize decoder.", CheckCategory.Error),
                    duration
                );
            pass2 = decodeResult;
        }

        progress.Report(1.0f);
        return new CheckOutcome(BuildResult([.. pass1, .. pass2]), duration);
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
