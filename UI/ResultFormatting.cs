using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.UI;

internal enum ResultSeverity
{
    None,
    Low,
    Medium,
    High,
    Critical,
}

[SupportedOSPlatform("windows")]
internal static class ResultFormatting
{
    internal static ResultSeverity GetSeverity(CheckCategory category) =>
        category switch
        {
            CheckCategory.Metadata => ResultSeverity.Low,
            CheckCategory.Index => ResultSeverity.Low,
            CheckCategory.Structure => ResultSeverity.Medium,
            CheckCategory.Error => ResultSeverity.High,
            CheckCategory.Corruption => ResultSeverity.Critical,
            _ => ResultSeverity.None,
        };

    internal static Color GetSeverityColor(ResultSeverity severity) =>
        severity switch
        {
            ResultSeverity.Medium => Color.Goldenrod,
            ResultSeverity.High => Color.OrangeRed,
            ResultSeverity.Critical => Color.Crimson,
            _ => SystemColors.WindowText,
        };

    internal static string GetCategoryDisplayName(CheckCategory category) =>
        category switch
        {
            CheckCategory.Metadata => "Metadata",
            CheckCategory.Index => "Index",
            CheckCategory.Structure => "Structural",
            CheckCategory.Corruption => "Corruption",
            CheckCategory.Error => "Error",
            _ => "",
        };

    internal static string BuildMessageText(CheckResult result)
    {
        if (result.Category == CheckCategory.Ok)
            return string.Empty;

        var msg = result.ErrorMessage ?? string.Empty;

        // Check in severity order so the most critical diagnostic wins when multiple are present.
        if (msg.Contains("FRAME_CRC_MISMATCH"))
            return "Audio data is corrupted";
        if (msg.Contains("TRUNCATED_STREAM"))
            return "File appears to be incomplete or cut off";
        if (msg.Contains("DECODE_ERROR"))
            return "Audio could not be decoded";
        if (msg.Contains("UNPARSEABLE_STREAM"))
            return "Audio stream could not be read";
        if (msg.Contains("LOST_SYNC"))
            return "Audio stream is interrupted mid-file";
        if (msg.Contains("BAD_HEADER"))
            return "A frame header is malformed";
        if (msg.Contains("JUNK_DATA"))
            return "File contains unexpected extra data";
        if (msg.Contains("XING_FRAME_COUNT_MISMATCH"))
            return "Variable bitrate index does not match the content";
        if (msg.Contains("INFO_FRAME_COUNT_MISMATCH"))
            return "Constant bitrate index does not match the content";
        if (msg.Contains("LAME_TAG_CRC_MISMATCH"))
            return "Encoder metadata checksum is invalid";

        // Generic error messages from the checker infrastructure
        if (msg.Contains("not found"))
            return "File could not be found";
        if (msg.Contains("too large"))
            return "File is too large to be analysed";
        if (msg.Contains("Cannot read"))
            return "File could not be read";
        if (msg.Contains("Cancelled"))
            return "Analysis was cancelled";
        if (msg.Contains("end of stream"))
            return "Decoder did not reach the end of the file";

        // Category fallback
        return result.Category switch
        {
            CheckCategory.Corruption => "Audio data is corrupted",
            CheckCategory.Error => "Analysis could not complete",
            CheckCategory.Structure => "Stream structure issue detected",
            CheckCategory.Index => "Seek index does not match the content",
            CheckCategory.Metadata => "Metadata tag inconsistency detected",
            _ => string.Empty,
        };
    }

    internal static string BuildDetailsText(CheckResult result)
    {
        if (result.Category == CheckCategory.Ok)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        if (result.ErrorTimecode.HasValue)
            builder.Append($"@ {result.ErrorTimecode.Value:hh\\:mm\\:ss\\.fff}  ");
        if (result.ErrorFrameIndex.HasValue)
            builder.Append($"[frame {result.ErrorFrameIndex.Value}]  ");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            if (builder.Length > 0)
                builder.Append("— ");
            builder.Append(result.ErrorMessage);
        }
        return builder.ToString().TrimEnd();
    }
}
