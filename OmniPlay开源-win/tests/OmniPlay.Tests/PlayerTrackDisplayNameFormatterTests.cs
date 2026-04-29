using OmniPlay.Core.Models.Playback;

namespace OmniPlay.Tests;

public sealed class PlayerTrackDisplayNameFormatterTests
{
    [Fact]
    public void Format_BuildsMacStyleAudioTrackName()
    {
        var displayName = PlayerTrackDisplayNameFormatter.Format(
            "音轨",
            2,
            "Main",
            "eng",
            "dts-hd ma",
            "5.1",
            isDefault: true);

        Assert.Equal("🇺🇸 英语 - Main (DTS-HD MA 5.1 / 默认)", displayName);
    }

    [Fact]
    public void Format_BuildsMacStyleSubtitleTrackName()
    {
        var displayName = PlayerTrackDisplayNameFormatter.Format(
            "字幕",
            7,
            "简体字幕",
            "chi",
            "hdmv_pgs_subtitle",
            isExternal: true);

        Assert.Equal("🇨🇳 中文 - 简体字幕 (PGS 图形字幕 / 外挂)", displayName);
    }

    [Theory]
    [InlineData("jpn", "🇯🇵 日语")]
    [InlineData("kor", "🇰🇷 韩语")]
    [InlineData("fra", "🇫🇷 法语")]
    [InlineData("deu", "🇩🇪 德语")]
    [InlineData("und", "UND")]
    public void TranslateLanguageCode_UsesMacStyleLabels(string language, string expected)
    {
        Assert.Equal(expected, PlayerTrackDisplayNameFormatter.TranslateLanguageCode(language));
    }
}
