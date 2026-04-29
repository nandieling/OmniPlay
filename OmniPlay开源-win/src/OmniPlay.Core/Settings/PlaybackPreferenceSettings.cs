namespace OmniPlay.Core.Settings;

public sealed record PlaybackPreferenceSettings
{
    public const string DefaultAudioSmart = "auto";
    public const string AudioChinese = "chi";
    public const string AudioEnglish = "eng";
    public const string AudioJapanese = "jpn";

    public const string DefaultSubtitleChinese = "chi";
    public const string SubtitleEnglish = "eng";

    public string DefaultAudioTrack { get; init; } = DefaultAudioSmart;

    public string DefaultSubtitleTrack { get; init; } = DefaultSubtitleChinese;
}
