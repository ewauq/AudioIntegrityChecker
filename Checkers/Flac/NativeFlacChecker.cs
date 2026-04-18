using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;
using AudioIntegrityChecker.Pipeline;

namespace AudioIntegrityChecker.Checkers.Flac;

/// <summary>
/// Verifies FLAC file integrity via P/Invoke against libFLAC.dll. Decode
/// runs against a pre-loaded <see cref="FileBuffer"/> so the file is read
/// exactly once. STREAMINFO is parsed from the same buffer before decoder
/// init, and all other metadata callbacks are suppressed via
/// FLAC__stream_decoder_set_metadata_ignore_all. Progress reports are
/// rate-limited to one per integer percent.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeFlacChecker : IFormatChecker
{
    public string FormatId => "FLAC";

    public bool SupportsMemoryMappedBuffer => true;

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

    // Error codes 0 (LOST_SYNC) and 1 (BAD_HEADER) are benign resync events;
    // 2 (FRAME_CRC_MISMATCH) and 3 (UNPARSEABLE_STREAM) indicate real corruption.
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

    private sealed class DecodeState
    {
        public IntPtr DataPtr;
        public int DataLength;
        public int BufferPosition;

        public ulong TotalSamples;
        public uint SampleRate;
        public ulong DecodedSamples;

        public bool HasError;
        public int ErrorStatus;
        public bool ErrorIsWarning;
        public ulong ErrorAtSample;
        public int ErrorAtBytePosition;

        public int LastReportedPercent = -1;

        public CancellationToken CancellationToken;
        public IProgress<FileProgress>? Progress;
        public bool Cancelled;
    }

    public CheckOutcome Check(
        FileBuffer buffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    ) => DecodeBuffer(buffer, cancellationToken, progress);

    private static CheckOutcome DecodeBuffer(
        FileBuffer buffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    )
    {
        // Pre-read STREAMINFO from the buffer, used for progress, timecode, and duration.
        // This avoids needing a metadata callback during decode, and saves a second disk
        // round-trip that would otherwise be required during the scan phase.
        var (totalSamples, sampleRate) = FlacMetadataReader.TryReadStreamInfo(buffer.AsSpan());
        TimeSpan? duration =
            totalSamples > 0 && sampleRate > 0
                ? TimeSpan.FromSeconds((double)totalSamples / sampleRate)
                : null;

        var state = new DecodeState
        {
            DataPtr = buffer.Pointer,
            DataLength = buffer.Length,
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

                bool isTrailingGarbage = IsTrailingGarbage(state);

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

    private static unsafe int OnRead(
        IntPtr decoder,
        IntPtr buffer,
        ref UIntPtr byteCount,
        IntPtr clientData
    )
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        int requested = (int)(ulong)byteCount;
        int remaining = state.DataLength - state.BufferPosition;

        if (remaining <= 0)
        {
            byteCount = UIntPtr.Zero;
            return 1; // END_OF_STREAM
        }

        int toRead = Math.Min(requested, remaining);
        Buffer.MemoryCopy(
            (byte*)state.DataPtr + state.BufferPosition,
            (byte*)buffer,
            toRead,
            toRead
        );
        state.BufferPosition += toRead;
        byteCount = (UIntPtr)(uint)toRead;
        return 0; // CONTINUE
    }

    private static int OnSeek(IntPtr decoder, ulong offset, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        if (offset > (ulong)state.DataLength)
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
        streamLength = (ulong)state.DataLength;
        return 0;
    }

    private static bool OnEof(IntPtr decoder, IntPtr clientData)
    {
        var state = (DecodeState)GCHandle.FromIntPtr(clientData).Target!;
        return state.BufferPosition >= state.DataLength;
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
        state.ErrorAtBytePosition = state.BufferPosition;
        state.HasError = true;
    }

    // Heuristic: when libFLAC reports LOST_SYNC past the declared sample
    // count, or when STREAMINFO is unreadable but the error fires within
    // the last few KB of the file and the trailing bytes look like an ID3
    // tag, treat it as trailing garbage rather than a mid-stream break.
    private static bool IsTrailingGarbage(DecodeState state)
    {
        if (state.ErrorStatus != 0) // not LOST_SYNC
            return false;

        if (state.TotalSamples > 0 && state.ErrorAtSample >= state.TotalSamples)
            return true;

        if (state.DecodedSamples == 0)
            return false; // nothing decoded, this is not an end-of-stream event

        // STREAMINFO missing or total_samples == 0 (valid per spec for streamed FLAC).
        // Fall back to a byte-level check: if the error position is close to EOF and
        // the trailing bytes carry an ID3v1 / ID3v2 signature, it's trailing garbage.
        int tailStart = Math.Max(0, state.ErrorAtBytePosition - 16);
        int tailLength = state.DataLength - tailStart;
        if (tailLength <= 0 || tailLength > 8192)
            return false;

        return ContainsId3Signature(state.DataPtr, tailStart, tailLength);
    }

    private static unsafe bool ContainsId3Signature(IntPtr dataPtr, int offset, int length)
    {
        byte* p = (byte*)dataPtr + offset;
        for (int i = 0; i <= length - 3; i++)
        {
            // ID3v1 (TAG) at EOF-128 or ID3v2 (ID3) prefix
            if (p[i] == 0x54 && p[i + 1] == 0x41 && p[i + 2] == 0x47)
                return true;
            if (p[i] == 0x49 && p[i + 1] == 0x44 && p[i + 2] == 0x33)
                return true;
        }
        return false;
    }
}
