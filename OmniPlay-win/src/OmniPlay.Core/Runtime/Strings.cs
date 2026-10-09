namespace OmniPlay.Core.Runtime;

/// <summary>
/// 中文原文到英文的替换表。界面以中文为源语言，切到英文时按本表整体替换；
/// 表由 <c>tools/i18n</c> 从 XAML 与代码里提取的文案清单维护。
/// </summary>
public static class Strings
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Normalized = new(StringComparer.Ordinal);

    static Strings()
    {
        foreach (var pair in Table)
        {
            English[pair.Key] = pair.Value;
        }
    }

    public static int Count => English.Count + Normalized.Count;

    public static bool TryTranslate(string text, out string english)
    {
        if (English.TryGetValue(text, out var found) || Normalized.TryGetValue(text, out found))
        {
            english = found;
            return true;
        }

        english = string.Empty;
        return false;
    }

    /// <summary>运行期补充词条（例如按模板生成的文案）。</summary>
    public static void Register(string chinese, string english) => Normalized[chinese] = english;

    /// <summary>是否存在该中文文案的英文翻译，供测试与提取工具核对覆盖率。</summary>
    public static bool HasTranslation(string chinese) =>
        English.ContainsKey(chinese) || Normalized.ContainsKey(chinese);

    private static readonly KeyValuePair<string, string>[] Table =
    [
        new("关闭", "Close"),
        new("保存", "Save"),
        new("取消", "Cancel"),
    ];
}
