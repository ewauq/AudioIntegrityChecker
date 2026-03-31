using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Checkers.Flac;

/// <summary>
/// Verifies FLAC file integrity via P/Invoke against libFLAC.dll.
///
/// Strategy:
///   1. Load the entire file into a managed byte[] before decoding.
///      The FLAC decoder reads from that in-memory buffer via callbacks —
///      zero disk I/O during decode, safe for multithreaded use.
///   2. Pre-read STREAMINFO (42 bytes) to obtain total_samples and sample_rate
///      before decoder init, so no metadata callback is needed.
///   3. Call FLAC__stream_decoder_set_metadata_ignore_all to suppress all
///      metadata callbacks (avoids parsing large cover art / Vorbis comments).
///   4. Rate-limit progress reports to at most one per integer-percent change
///      (≤ 100 BeginInvoke calls per file regardless of frame count).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeFlacChecker : IFormatChecker
{
    public string FormatId => "FLAC";

    private const string LibFlac = "libFLAC.dll";

    [DllImport(LibFlac, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FLAC__stream_decoder_new();

    [DllImport(LibFlac, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FLAC__stream_decoder_delete(IntPtr decoder);

    [DllImport(LibFlac, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FLAC__stream_decoder_set_metadata_ignore_all(IntPtr decoder);

    [DllImport(LibFlac, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FLAC__stream_decoder_init_stream(
        IntPtr decoder,
        ReadCallback readCallback,
        SeekCallback seekCallback,
        TellCallback tellCallback,
        LengthCallback lengthCallback,
        EofCallback eofCallback,
        WriteCallback writeCallback,
        IntPtr metadataCallback, // always null — ignored via set_metadata_ignore_all
        ErrorCallback errorCallback,
        IntPtr clientData
    );

    [DllImport(LibFlac, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FLAC__stream_decoder_process_until_end_of_stream(IntPtr decoder);

    [DllImport(LibFlac, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FLAC__stream_decoder_finish(IntPtr decoder);

    // Returns: 0=CONTINUE, 1=END_OF_STREAM, 2=ABORT
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadCallback(
        IntPtr decoder,
        IntPtr buffer,
        ref UIntPtr byteCount,
        IntPtr clientData
    );

    // Returns: 0=OK, 1=ERROR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekCallback(IntPtr decoder, ulong absoluteOffset, IntPtr clientData);

    // Returns: 0=OK, 1=ERROR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TellCallback(IntPtr decoder, ref ulong absoluteOffset, IntPtr clientData);

    // Returns: 0=OK, 1=ERROR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LengthCallback(IntPtr decoder, ref ulong streamLength, IntPtr clientData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EofCallback(IntPtr decoder, IntPtr clientData);

    // Returns: 0=CONTINUE, 1=ABORT
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteCallback(
        IntPtr decoder,
        IntPtr framePtr,
        IntPtr samples,
        IntPtr clientData
    );

    // status: 0=LOST_SYNC, 1=BAD_HEADER, 2=FRAME_CRC_MISMATCH, 3=UNPARSEABLE_STREAM
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ErrorCallback(IntPtr decoder, int status, IntPtr clientData);

    // -------------------------------------------------------------------------
    // FLAC__FrameHeader layout (partial — only fields we read)
    // -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FlacFrameHeader
    {
        public uint BlockSize;
        public uint SampleRate;
        public uint Channels;
        public uint ChannelAssignment;
        public uint BitsPerSample;
        public int NumberType;
        public ulong FrameOrSampleNumber;
    }

    // -------------------------------------------------------------------------
    // Error severity classification
    //   0 LOST_SYNC          → WARNING (trailing garbage / benign resync)
    //   1 BAD_HEADER         → WARNING (unreadable header, audio may be intact)
    //   2 FRAME_CRC_MISMATCH → ERROR   (audio data corrupted)
    //   3 UNPARSEABLE_STREAM → ERROR   (fundamental format violation)
    // -------------------------------------------------------------------------

    private static bool IsBenignError(int status) => status is 0 or 1;

    private static string ErrorStatusName(int status) =>
        status switch
        {
            0 => "LOST_SYNC",
            1 => "BAD_HEADER",
            2 => "FRAME_CRC_MISMATCH",
            3 => "UNPARSEABLE_STREAM",
            _ => $"STATUS_{status}",
        };

    // -------------------------------------------------------------------------
    // Per-decode state (passed to libFLAC callbacks via GCHandle)
    // -------------------------------------------------------------------------

    private sealed class DecodeState
    {
        public required byte[] FileBuffer;
        public int BufferPosition;

        public ulong TotalSamples;
        public uint SampleRate;
        public ulong DecodedSamples;

        public bool HasError;
        public int ErrorStatus;
        public bool ErrorIsWarning;
        public ulong ErrorAtSample;

        public int LastReportedPercent = -1;

        public CancellationToken CancellationToken;
        public IProgress<FileProgress>? Progress;
        public bool Cancelled;
    }

    public CheckResult Check(
        string filePath,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    )
    {
        if (!File.Exists(filePath))
            return CheckResult.Error("File not found.", CheckCategory.Error);

        if (!IsLibraryAvailable())
            return CheckResult.Error("libFLAC.dll not found.", CheckCategory.Error);

        byte[] fileBuffer;
        try
        {
            fileBuffer = File.ReadAllBytes(filePath);
        }
        catch (OutOfMemoryException)
        {
            return CheckResult.Error("File too large to load into memory.", CheckCategory.Error);
        }
        catch (Exception ex)
        {
            return CheckResult.Error($"Cannot read file: {ex.Message}", CheckCategory.Error);
        }

        // Pre-read STREAMINFO from the buffer — used for progress and timecode.
        // This avoids needing a metadata callback during decode.
        var (totalSamples, sampleRate) = FlacMetadataReader.TryReadStreamInfo(fileBuffer);

        var state = new DecodeState
        {
            FileBuffer = fileBuffer,
            TotalSamples = totalSamples,
            SampleRate = sampleRate,
            CancellationToken = cancellationToken,
            Progress = progress,
        };

        var gcHandle = GCHandle.Alloc(state);
        var decoder = IntPtr.Zero;

        try
        {
            decoder = FLAC__stream_decoder_new();
            if (decoder == IntPtr.Zero)
                return CheckResult.Error("Failed to allocate FLAC decoder.", CheckCategory.Error);

            // Suppress all metadata callbacks — we pre-read what we need.
            FLAC__stream_decoder_set_metadata_ignore_all(decoder);

            // Keep delegate instances alive for the full duration of decode.
            ReadCallback readCallback = OnRead;
            SeekCallback seekCallback = OnSeek;
            TellCallback tellCallback = OnTell;
            LengthCallback lengthCallback = OnLength;
            EofCallback eofCallback = OnEof;
            WriteCallback writeCallback = OnWrite;
            ErrorCallback errorCallback = OnError;

            var clientData = GCHandle.ToIntPtr(gcHandle);

            int initStatus = FLAC__stream_decoder_init_stream(
                decoder,
                readCallback,
                seekCallback,
                tellCallback,
                lengthCallback,
                eofCallback,
                writeCallback,
                IntPtr.Zero,
                errorCallback,
                clientData
            );

            if (initStatus != 0)
                return CheckResult.Error(
                    $"FLAC decoder init failed (status {initStatus}).",
                    CheckCategory.Error
                );

            bool decodeSucceeded = FLAC__stream_decoder_process_until_end_of_stream(decoder);
            FLAC__stream_decoder_finish(decoder);

            if (state.Cancelled)
                return CheckResult.Error("Cancelled.", CheckCategory.Error);

            if (state.HasError)
            {
                TimeSpan? timecode =
                    state.SampleRate > 0
                        ? TimeSpan.FromSeconds((double)state.ErrorAtSample / state.SampleRate)
                        : null;

                // LOST_SYNC at or after the STREAMINFO sample count means the decoder
                // hit non-audio data (an ID3 tag or padding) appended after the last
                // audio frame — not a mid-stream interruption.
                bool isTrailingGarbage =
                    state.ErrorStatus == 0 // LOST_SYNC
                    && state.TotalSamples > 0
                    && state.ErrorAtSample >= state.TotalSamples;

                string message = isTrailingGarbage
                    ? "TRAILING_GARBAGE"
                    : ErrorStatusName(state.ErrorStatus);

                // status 0 (LOST_SYNC) and 1 (BAD_HEADER) → stream structure anomaly
                // status 2 (FRAME_CRC_MISMATCH) and 3 (UNPARSEABLE_STREAM) → audio data corrupt
                var category = IsBenignError(state.ErrorStatus)
                    ? CheckCategory.Structure
                    : CheckCategory.Corruption;

                return state.ErrorIsWarning || isTrailingGarbage
                    ? CheckResult.Warning(message, category, timecode, (long)state.ErrorAtSample)
                    : CheckResult.Error(message, category, timecode, (long)state.ErrorAtSample);
            }

            if (!decodeSucceeded)
                return CheckResult.Error(
                    "Decoder did not reach end of stream cleanly.",
                    CheckCategory.Corruption
                );

            return CheckResult.Ok();
        }
        finally
        {
            if (decoder != IntPtr.Zero)
                FLAC__stream_decoder_delete(decoder);
            gcHandle.Free();
        }
    }

    private static int OnRead(
        IntPtr decoder,
        IntPtr buffer,
        ref UIntPtr byteCount,
        IntPtr clientData
    )
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        int requested = (int)(ulong)byteCount;
        int remaining = state.FileBuffer.Length - state.BufferPosition;

        if (remaining <= 0)
        {
            byteCount = UIntPtr.Zero;
            return 1; // END_OF_STREAM
        }

        int toRead = Math.Min(requested, remaining);
        Marshal.Copy(state.FileBuffer, state.BufferPosition, buffer, toRead);
        state.BufferPosition += toRead;
        byteCount = (UIntPtr)(uint)toRead;
        return 0; // CONTINUE
    }

    private static int OnSeek(IntPtr decoder, ulong offset, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        if (offset > (ulong)state.FileBuffer.Length)
            return 1; // ERROR
        state.BufferPosition = (int)offset;
        return 0;
    }

    private static int OnTell(IntPtr decoder, ref ulong absoluteOffset, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        absoluteOffset = (ulong)state.BufferPosition;
        return 0;
    }

    private static int OnLength(IntPtr decoder, ref ulong streamLength, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        streamLength = (ulong)state.FileBuffer.Length;
        return 0;
    }

    private static bool OnEof(IntPtr decoder, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        return state.BufferPosition >= state.FileBuffer.Length;
    }

    private static int OnWrite(IntPtr decoder, IntPtr framePtr, IntPtr samples, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;

        if (state.CancellationToken.IsCancellationRequested)
        {
            state.Cancelled = true;
            return 1; // ABORT
        }

        var header = Marshal.PtrToStructure<FlacFrameHeader>(framePtr);
        state.DecodedSamples += header.BlockSize;

        if (state.TotalSamples > 0 && state.Progress != null)
        {
            float progressFraction = Math.Clamp(
                (float)state.DecodedSamples / state.TotalSamples,
                0f,
                1f
            );
            int percentComplete = (int)(progressFraction * 100f);

            if (percentComplete > state.LastReportedPercent)
            {
                state.LastReportedPercent = percentComplete;
                state.Progress.Report(progressFraction);
            }
        }

        return 0; // CONTINUE
    }

    private static void OnError(IntPtr decoder, int status, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        if (state.HasError)
            return; // capture first error only

        state.ErrorStatus = status;
        state.ErrorIsWarning = IsBenignError(status);
        state.ErrorAtSample = state.DecodedSamples;
        state.HasError = true;
    }

    private static bool? _libraryAvailable;

    internal static bool IsLibraryAvailable()
    {
        if (_libraryAvailable.HasValue)
            return _libraryAvailable.Value;

        if (NativeLibrary.TryLoad(LibFlac, out var handle))
        {
            NativeLibrary.Free(handle);
            _libraryAvailable = true;
        }
        else
        {
            _libraryAvailable = false;
        }

        return _libraryAvailable.Value;
    }
}
