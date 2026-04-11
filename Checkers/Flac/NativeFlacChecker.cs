using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;
using AudioIntegrityChecker.Pipeline;

namespace AudioIntegrityChecker.Checkers.Flac;

/// <summary>
/// Verifies FLAC file integrity via P/Invoke against libFLAC.dll.
///
/// Strategy:
///   1. Decode runs against a pre-loaded in-memory buffer (either supplied
///      by the pipeline via <see cref="IBufferedChecker"/> or loaded by the
///      legacy <see cref="Check(string, CancellationToken, IProgress{FileProgress})"/>
///      overload). The FLAC decoder reads from that buffer via callbacks, so
///      there is zero disk I/O during decode and the path is safe for
///      multithreaded use.
///   2. Pre-read STREAMINFO (42 bytes) to obtain total_samples and sample_rate
///      before decoder init, so no metadata callback is needed.
///   3. Call FLAC__stream_decoder_set_metadata_ignore_all to suppress all
///      metadata callbacks (avoids parsing large cover art / Vorbis comments).
///   4. Rate-limit progress reports to at most one per integer-percent change
///      (≤ 100 BeginInvoke calls per file regardless of frame count).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeFlacChecker : IFormatChecker, IBufferedChecker
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
        IntPtr metadataCallback, // always null, ignored via set_metadata_ignore_all
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

    // Cached delegate instances: the callbacks have no captured state (context is
    // read from client_data), so the same delegate is reused for every decode and
    // marshalling allocates only once per process instead of once per file.
    private static readonly ReadCallback s_readCallback = OnRead;
    private static readonly SeekCallback s_seekCallback = OnSeek;
    private static readonly TellCallback s_tellCallback = OnTell;
    private static readonly LengthCallback s_lengthCallback = OnLength;
    private static readonly EofCallback s_eofCallback = OnEof;
    private static readonly WriteCallback s_writeCallback = OnWrite;
    private static readonly ErrorCallback s_errorCallback = OnError;

    // -------------------------------------------------------------------------
    // FLAC__FrameHeader layout (partial, only fields we read)
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

    public CheckOutcome Check(
        string filePath,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    )
    {
        if (!File.Exists(filePath))
            return new CheckOutcome(
                CheckResult.Error("File not found.", CheckCategory.Error),
                null
            );

        if (!IsLibraryAvailable())
            return new CheckOutcome(
                CheckResult.Error("libFLAC.dll not found.", CheckCategory.Error),
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
            return DecodeBuffer(buffer.AsArray(), cancellationToken, progress);
    }

    CheckOutcome IBufferedChecker.CheckWithBuffer(
        string filePath,
        FileBuffer buffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    )
    {
        if (!IsLibraryAvailable())
            return new CheckOutcome(
                CheckResult.Error("libFLAC.dll not found.", CheckCategory.Error),
                null
            );

        return DecodeBuffer(buffer.AsArray(), cancellationToken, progress);
    }

    private static CheckOutcome DecodeBuffer(
        byte[] fileBuffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    )
    {
        // Pre-read STREAMINFO from the buffer, used for progress, timecode, and duration.
        // This avoids needing a metadata callback during decode, and saves a second disk
        // round-trip that would otherwise be required during the scan phase.
        var (totalSamples, sampleRate) = FlacMetadataReader.TryReadStreamInfo(fileBuffer);
        TimeSpan? duration =
            totalSamples > 0 && sampleRate > 0
                ? TimeSpan.FromSeconds((double)totalSamples / sampleRate)
                : null;

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
                return new CheckOutcome(
                    CheckResult.Error("Failed to allocate FLAC decoder.", CheckCategory.Error),
                    duration
                );

            // Suppress all metadata callbacks: we pre-read what we need.
            FLAC__stream_decoder_set_metadata_ignore_all(decoder);

            var clientData = GCHandle.ToIntPtr(gcHandle);

            int initStatus = FLAC__stream_decoder_init_stream(
                decoder,
                s_readCallback,
                s_seekCallback,
                s_tellCallback,
                s_lengthCallback,
                s_eofCallback,
                s_writeCallback,
                IntPtr.Zero,
                s_errorCallback,
                clientData
            );

            if (initStatus != 0)
                return new CheckOutcome(
                    CheckResult.Error(
                        $"FLAC decoder init failed (status {initStatus}).",
                        CheckCategory.Error
                    ),
                    duration
                );

            bool decodeSucceeded = FLAC__stream_decoder_process_until_end_of_stream(decoder);
            FLAC__stream_decoder_finish(decoder);

            if (state.Cancelled)
                return new CheckOutcome(
                    CheckResult.Error("Cancelled.", CheckCategory.Error),
                    duration
                );

            if (state.HasError)
            {
                TimeSpan? timecode =
                    state.SampleRate > 0
                        ? TimeSpan.FromSeconds((double)state.ErrorAtSample / state.SampleRate)
                        : null;

                // LOST_SYNC at or after the STREAMINFO sample count means the decoder
                // hit non-audio data (an ID3 tag or padding) appended after the last
                // audio frame, not a mid-stream interruption.
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

                var errorResult =
                    state.ErrorIsWarning || isTrailingGarbage
                        ? CheckResult.Warning(
                            message,
                            category,
                            timecode,
                            (long)state.ErrorAtSample
                        )
                        : CheckResult.Error(message, category, timecode, (long)state.ErrorAtSample);
                return new CheckOutcome(errorResult, duration);
            }

            if (!decodeSucceeded)
                return new CheckOutcome(
                    CheckResult.Error(
                        "Decoder did not reach end of stream cleanly.",
                        CheckCategory.Corruption
                    ),
                    duration
                );

            return new CheckOutcome(CheckResult.Ok(), duration);
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
