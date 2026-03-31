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
            return "Audio data corrupted";
        if (msg.Contains("TRUNCATED_STREAM"))
            return "File truncated";
        if (msg.Contains("DECODE_ERROR"))
            return "Decode failure";
        if (msg.Contains("UNPARSEABLE_STREAM"))
            return "Unreadable stream";
        if (msg.Contains("LOST_SYNC"))
            return "Sync lost";
        if (msg.Contains("BAD_HEADER"))
            return "Invalid header";
        if (msg.Contains("JUNK_DATA"))
            return "Junk data present";
        if (msg.Contains("XING_FRAME_COUNT_MISMATCH"))
            return "VBR index incorrect";
        if (msg.Contains("INFO_FRAME_COUNT_MISMATCH"))
            return "CBR index incorrect";
        if (msg.Contains("LAME_TAG_CRC_MISMATCH"))
            return "LAME tag CRC error";

        // Generic error messages from the checker infrastructure
        if (msg.Contains("not found"))
            return "File not found";
        if (msg.Contains("too large"))
            return "File too large";
        if (msg.Contains("Cannot read"))
            return "Cannot read file";
        if (msg.Contains("Cancelled"))
            return "Cancelled";
        if (msg.Contains("end of stream"))
            return "Incomplete decode";

        // Category fallback
        return result.Category switch
        {
            CheckCategory.Corruption => "Audio data corrupted",
            CheckCategory.Error => "Analysis failed",
            CheckCategory.Structure => "Stream anomaly",
            CheckCategory.Index => "Index mismatch",
            CheckCategory.Metadata => "Tag inconsistency",
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
