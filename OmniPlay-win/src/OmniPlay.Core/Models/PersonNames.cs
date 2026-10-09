using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmniPlay.Core.Models;

/// <summary>
/// 导演/主演名单的统一编解码。数据库以 JSON 数组保存全量名单（与 mac 版 <c>Movie.directors/casts</c> 对齐），
/// 同时保留早期版本的扁平字符串列（顿号/逗号分隔）作为降级来源与展示兼容。
/// </summary>
public static class PersonNames
{
    private static readonly Regex HanRegex = new(@"\p{IsCJKUnifiedIdeographs}", RegexOptions.Compiled);
    private static readonly Regex SeparatorRegex = new("[、,，;；/|]+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>把名单编码成入库用的 JSON 字符串；空名单返回 null（列保持 NULL 表示"未刮削"）。</summary>
    public static string? Encode(IReadOnlyList<string>? names)
    {
        var normalized = Normalize(names);
        if (normalized.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    /// <summary>解析名单：优先按 JSON 数组读取，非 JSON 时按分隔符拆分（兼容历史扁平列）。</summary>
    public static List<string> Decode(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (text[0] == '[')
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(text);
                return Normalize(parsed);
            }
            catch (JsonException)
            {
                // 落到分隔符解析，保证脏数据不会让首页分类崩掉。
            }
        }

        return Split(text);
    }

    /// <summary>把扁平字符串按顿号/逗号/分号拆成名单。</summary>
    public static List<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return Normalize(SeparatorRegex.Split(raw));
    }

    /// <summary>把 JSON 名单转成扁平展示串，供历史列与既有 UI 使用。</summary>
    public static string? Flatten(string? encoded, string separator = "、")
    {
        var names = Decode(encoded);
        return names.Count == 0 ? null : string.Join(separator, names);
    }

    public static bool ContainsChinese(string? value) =>
        !string.IsNullOrEmpty(value) && HanRegex.IsMatch(value);

    /// <summary>去空白、去重、丢弃超长项。</summary>
    public static List<string> Normalize(IEnumerable<string>? names)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (names is null)
        {
            return result;
        }

        foreach (var name in names)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 120)
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
