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
