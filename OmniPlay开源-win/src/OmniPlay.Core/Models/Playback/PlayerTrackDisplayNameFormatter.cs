namespace OmniPlay.Core.Models.Playback;

public static class PlayerTrackDisplayNameFormatter
{
    public static string Format(
        string fallbackPrefix,
        long trackId,
        string? title,
        string? language,
        string? codec = null,
        string? audioChannels = null,
        bool isDefault = false,
        bool isForced = false,
        bool isExternal = false)
    {
        var rawTitle = title?.Trim() ?? string.Empty;
        var rawLanguage = language?.Trim() ?? string.Empty;
        var languageLabel = TranslateLanguageCode(rawLanguage);
        var displayParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(languageLabel))
        {
            displayParts.Add(languageLabel);
        }

        if (!string.IsNullOrWhiteSpace(rawTitle) &&
            !string.Equals(rawTitle, rawLanguage, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rawTitle, "Surround 5.1", StringComparison.OrdinalIgnoreCase))
        {
            displayParts.Add(rawTitle);
        }

        var baseName = displayParts.Count == 0
            ? $"{fallbackPrefix} {trackId}"
            : string.Join(" - ", displayParts);

        var metadataParts = new List<string>();
        var formattedCodec = FormatCodec(codec);
        var formattedChannels = FormatAudioChannels(audioChannels);

        if (!string.IsNullOrWhiteSpace(formattedCodec) && !string.IsNullOrWhiteSpace(formattedChannels))
        {
            metadataParts.Add($"{formattedCodec} {formattedChannels}");
        }
        else if (!string.IsNullOrWhiteSpace(formattedCodec))
        {
            metadataParts.Add(formattedCodec);
        }
        else if (!string.IsNullOrWhiteSpace(formattedChannels))
        {
            metadataParts.Add(formattedChannels);
        }

        if (isDefault)
        {
            metadataParts.Add("默认");
        }

        if (isForced)
        {
            metadataParts.Add("强制");
        }

        if (isExternal)
        {
            metadataParts.Add("外挂");
        }

        return metadataParts.Count == 0
            ? baseName
            : $"{baseName} ({string.Join(" / ", metadataParts)})";
    }

    public static string TranslateLanguageCode(string? language)
    {
        var normalized = language?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "chi" or "zho" or "zh" or "zh-cn" or "zh-hans" or "zh-sg" or "chs" or "cmn" => "🇨🇳 中文",
            "zh-tw" or "zh-hant" or "cht" => "🇨🇳 中文",
            "zh-hk" or "yue" => "🇨🇳 中文",
            "eng" or "en" or "en-us" or "en-gb" => "🇺🇸 英语",
            "jpn" or "ja" or "ja-jp" => "🇯🇵 日语",
            "kor" or "ko" => "🇰🇷 韩语",
            "fre" or "fra" or "fr" => "🇫🇷 法语",
            "spa" or "es" => "🇪🇸 西语",
            "ger" or "deu" or "de" => "🇩🇪 德语",
            "rus" or "ru" => "🇷🇺 俄语",
            "ita" or "it" => "🇮🇹 意语",
            "por" or "pt" => "🇵🇹 葡语",
            "tha" or "th" => "🇹🇭 泰语",
            "vie" or "vi" => "🇻🇳 越南语",
            null or "" => string.Empty,
            _ => normalized.ToUpperInvariant()
        };
    }

    public static string FormatCodec(string? codec)
    {
        var normalized = codec?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.Contains("truehd", StringComparison.OrdinalIgnoreCase))
        {
            return "TrueHD Atmos";
        }

        if (normalized.Contains("dts-hd", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("dtshd", StringComparison.OrdinalIgnoreCase))
        {
            return "DTS-HD MA";
        }

        if (normalized.Contains("dts", StringComparison.OrdinalIgnoreCase))
        {
            return "DTS";
        }

        if (normalized.Contains("eac3", StringComparison.OrdinalIgnoreCase))
        {
            return "E-AC3";
        }

        if (normalized.Contains("ac3", StringComparison.OrdinalIgnoreCase))
        {
            return "Dolby AC3";
        }

        if (normalized.Contains("aac", StringComparison.OrdinalIgnoreCase))
        {
            return "AAC";
        }

        if (normalized.Contains("flac", StringComparison.OrdinalIgnoreCase))
        {
            return "FLAC";
        }

        if (normalized.Contains("pgs", StringComparison.OrdinalIgnoreCase))
        {
            return "PGS 图形字幕";
        }

        if (normalized.Contains("srt", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("subrip", StringComparison.OrdinalIgnoreCase))
        {
            return "SRT";
        }

        if (normalized.Contains("ass", StringComparison.OrdinalIgnoreCase))
        {
            return "ASS";
        }

        return normalized.ToUpperInvariant();
    }

    public static string FormatAudioChannels(string? channels)
    {
        var normalized = channels?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.ToLowerInvariant() switch
        {
            "stereo" => "2.0",
            "mono" => "1.0",
            _ => normalized
        };
    }
}
