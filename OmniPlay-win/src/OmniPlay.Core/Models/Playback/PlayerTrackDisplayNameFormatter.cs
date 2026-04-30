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
        var languageLabel = SimplifyLanguageLabel(TranslateLanguageCode(rawLanguage));
        var displayParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(languageLabel))
        {
            displayParts.Add(languageLabel);
        }

        if (!string.IsNullOrWhiteSpace(rawTitle) &&
            !IsDuplicateTrackTitle(rawTitle, rawLanguage, languageLabel) &&
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
            : $"{baseName} ({string.Join("/", metadataParts)})";
    }

    private static bool IsDuplicateTrackTitle(string title, string language, string languageLabel)
    {
        if (string.Equals(title, language, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(title, languageLabel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedTitle = NormalizeTrackLabelForComparison(title);
        var normalizedLanguage = NormalizeTrackLabelForComparison(language);
        var normalizedLanguageLabel = NormalizeTrackLabelForComparison(languageLabel);

        return (!string.IsNullOrWhiteSpace(normalizedLanguage) &&
                string.Equals(normalizedTitle, normalizedLanguage, StringComparison.OrdinalIgnoreCase)) ||
            GetLanguageAliases(language, languageLabel)
                .Select(NormalizeTrackLabelForComparison)
                .Where(static alias => !string.IsNullOrWhiteSpace(alias))
                .Any(alias => string.Equals(normalizedTitle, alias, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(normalizedLanguageLabel) &&
             (string.Equals(normalizedTitle, normalizedLanguageLabel, StringComparison.OrdinalIgnoreCase) ||
              normalizedTitle.Contains(normalizedLanguageLabel, StringComparison.OrdinalIgnoreCase) ||
              normalizedLanguageLabel.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> GetLanguageAliases(string language, string languageLabel)
    {
        if (!string.IsNullOrWhiteSpace(languageLabel))
        {
            yield return languageLabel;
        }

        foreach (var token in language.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "eng":
                case "en":
                    yield return "English";
                    yield return "\u82F1\u8BED";
                    break;
                case "jpn":
                case "ja":
                    yield return "Japanese";
                    yield return "\u65E5\u8BED";
                    break;
                case "kor":
                case "ko":
                    yield return "Korean";
                    yield return "\u97E9\u8BED";
                    break;
                case "chi":
                case "zho":
                case "zh":
                case "chs":
                case "cht":
                case "cmn":
                case "yue":
                    yield return "Chinese";
                    yield return "\u4E2D\u6587";
                    yield return "\u7B80\u4F53";
                    yield return "\u7E41\u4F53";
                    break;
                case "fre":
                case "fra":
                case "fr":
                    yield return "French";
                    yield return "\u6CD5\u8BED";
                    break;
                case "spa":
                case "es":
                    yield return "Spanish";
                    yield return "\u897F\u8BED";
                    break;
                case "ger":
                case "deu":
                case "de":
                    yield return "German";
                    yield return "\u5FB7\u8BED";
                    break;
                case "rus":
                case "ru":
                    yield return "Russian";
                    yield return "\u4FC4\u8BED";
                    break;
                case "ita":
                case "it":
                    yield return "Italian";
                    yield return "\u610F\u8BED";
                    break;
                case "por":
                case "pt":
                    yield return "Portuguese";
                    yield return "\u8461\u8BED";
                    break;
                case "tha":
                case "th":
                    yield return "Thai";
                    yield return "\u6CF0\u8BED";
                    break;
                case "vie":
                case "vi":
                    yield return "Vietnamese";
                    yield return "\u8D8A\u5357\u8BED";
                    break;
            }
        }
    }

    private static string SimplifyLanguageLabel(string value)
    {
        var trimmed = value.Trim();
        var lastSpace = trimmed.LastIndexOf(' ');
        return lastSpace >= 0 && lastSpace < trimmed.Length - 1
            ? trimmed[(lastSpace + 1)..].Trim()
            : trimmed;
    }

    private static string NormalizeTrackLabelForComparison(string value)
    {
        return new string(value
            .Where(static character => char.IsLetterOrDigit(character))
            .ToArray());
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
