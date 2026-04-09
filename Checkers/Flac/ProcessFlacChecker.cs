using System.Diagnostics;
using System.Text.RegularExpressions;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Checkers.Flac;

/// <summary>
/// Verifies FLAC file integrity by invoking <c>flac.exe --test --silent</c>.
/// Sample rate is read from the FLAC STREAMINFO header (not via metaflac.exe)
/// to convert any sample-offset errors to a timecode.
/// </summary>
public sealed class ProcessFlacChecker : IFormatChecker
{
    public string FormatId => "FLAC";

    private const string FlacExecutable = "flac.exe";

    private static readonly Regex ErrorLineRegex = new(
        @"ERROR",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex SampleOffsetRegex = new(
        @"(?:sample|offset)[^\d]*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

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

        if (!ToolAvailable(FlacExecutable))
            return new CheckOutcome(
                CheckResult.Error(
                    $"{FlacExecutable} not found in PATH or application directory.",
                    CheckCategory.Error
                ),
                null
            );

        // Read STREAMINFO once, used both for timecode conversion and to expose the
        // track duration to the UI (which no longer reads it during the scan phase).
        var (totalSamples, sampleRate) = FlacMetadataReader.TryReadStreamInfo(filePath);
        TimeSpan? duration =
            totalSamples > 0 && sampleRate > 0
                ? TimeSpan.FromSeconds((double)totalSamples / sampleRate)
                : null;

        var result = RunFlacTest(filePath, sampleRate, cancellationToken, progress);
        return new CheckOutcome(result, duration);
    }

    private static CheckResult RunFlacTest(
        string filePath,
        uint sampleRate,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveToolPath(FlacExecutable)!,
            Arguments = $"--test --silent \"{filePath}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        string? firstErrorLine = null;
        long? errorSampleOffset = null;
        int linesProcessed = 0;

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
                return;
            linesProcessed++;

            // Provide an approximate indeterminate progress signal.
            progress?.Report(Math.Clamp(linesProcessed * 0.01f, 0f, 0.99f));

            if (firstErrorLine is not null)
                return;

            if (ErrorLineRegex.IsMatch(args.Data))
            {
                firstErrorLine = args.Data;
                var match = SampleOffsetRegex.Match(args.Data);
                if (match.Success && long.TryParse(match.Groups[1].Value, out var offset))
                    errorSampleOffset = offset;
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        while (!process.WaitForExit(200))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch { }
                return CheckResult.Error("Cancelled.", CheckCategory.Error);
            }
        }

        process.WaitForExit(); // drain async readers

        progress?.Report(1.0f);

        if (process.ExitCode != 0 || firstErrorLine is not null)
        {
            TimeSpan? timecode = null;
            if (errorSampleOffset.HasValue && sampleRate > 0)
                timecode = TimeSpan.FromSeconds((double)errorSampleOffset.Value / sampleRate);

            return CheckResult.Error(
                firstErrorLine ?? $"flac.exe exited with code {process.ExitCode}",
                CheckCategory.Corruption,
                timecode,
                errorSampleOffset
            );
        }

        return CheckResult.Ok();
    }

    private static bool ToolAvailable(string executable) => ResolveToolPath(executable) is not null;

    private static string? ResolveToolPath(string executable)
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, executable);
        if (File.Exists(localPath))
            return localPath;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (
            var directory in pathEnv.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            var candidate = Path.Combine(directory.Trim(), executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
