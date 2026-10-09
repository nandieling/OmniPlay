namespace OmniPlay.Core.Runtime;

/// <summary>
/// 运行时界面语言。中文是源语言（代码与 XAML 里直接写中文），
/// 英文通过 <see cref="Strings"/> 表在渲染时替换；查不到的条目保留中文，
/// 因此新增文案不会因为漏翻译而显示成 key。
/// </summary>
public static class Locale
{
    public const string Chinese = "zh-Hans";
    public const string English = "en";

    private static string current = Chinese;

    public static string Current => current;

    public static bool IsEnglish => string.Equals(current, English, StringComparison.OrdinalIgnoreCase);

    /// <summary>语言变化（设置页切换、启动时读取设置）。</summary>
    public static event Action? Changed;

    public static void Set(string? language)
    {
        var next = string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Chinese;
        if (string.Equals(current, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        current = next;
        Changed?.Invoke();
    }

    /// <summary>按当前语言取文案；无对应英文时原样返回。</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsEnglish)
        {
            return text ?? string.Empty;
        }

        return Strings.TryTranslate(text, out var english) ? english : text;
    }

    /// <summary>带占位符的文案：模板按中文原文查表后再格式化。</summary>
    public static string Fmt(string template, params object?[] args)
    {
        try
        {
            return args.Length == 0 ? T(template) : string.Format(T(template), args);
        }
        catch (FormatException)
        {
            return T(template);
        }
    }
}
