namespace OmniPlay.Core.Models.Library;

/// <summary>
/// 详情页"版本"条目：以一个 BDMV 碟目录 / ISO 镜像 / 松散文件所在目录为单位的影片版本。
/// </summary>
public sealed class DetailVersionItem
{
    public int Index { get; init; }

    public string Label { get; init; } = string.Empty;

    public string KeyPath { get; init; } = string.Empty;

    public int FileCount { get; init; }

    public LibraryVideoItem PrimaryFile { get; init; } = null!;

    public string DisplayTitle => $"\u7248\u672C {Index} \u00B7 {Label}";

    public string DisplayPath => KeyPath;

    public string DisplayStatus =>
        FileCount > 1
            ? $"{FileCount} \u4E2A\u6587\u4EF6 \u00B7 {PrimaryFile.PositionText} / {PrimaryFile.DurationText} \u00B7 {PrimaryFile.ProgressText}"
            : $"{PrimaryFile.PositionText} / {PrimaryFile.DurationText} \u00B7 {PrimaryFile.ProgressText}";
}