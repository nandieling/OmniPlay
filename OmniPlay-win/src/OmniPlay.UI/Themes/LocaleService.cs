using Avalonia;
using Avalonia.Controls;
using OmniPlay.Core.Runtime;

namespace OmniPlay.UI.Themes;

/// <summary>
/// 应用级文案服务：把当前语言的文案表作为一个合并字典挂到
/// <see cref="Application.Resources"/> 上，XAML 里的 <c>{DynamicResource L001}</c>
/// 会跟着语言即时切换，无需重建控件。
///
/// 与 <see cref="ThemeService"/> 一样按引用替换自己的字典，
/// 不去占用固定的下标，否则会把主题字典挤掉。
/// </summary>
public static class LocaleService
{
    private static ResourceDictionary? activeDictionary;

    public static void Apply(string? language)
    {
        Locale.Set(language);

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        application.Resources ??= new ResourceDictionary();
        var replacement = BuildDictionary(Locale.Current);
        var merged = application.Resources.MergedDictionaries;

        if (activeDictionary is not null)
        {
            for (var index = 0; index < merged.Count; index++)
            {
                if (ReferenceEquals(merged[index], activeDictionary))
                {
                    merged[index] = replacement;
                    activeDictionary = replacement;
                    return;
                }
            }
        }

        merged.Add(replacement);
        activeDictionary = replacement;
    }

    private static ResourceDictionary BuildDictionary(string language)
    {
        var dictionary = new ResourceDictionary();
        foreach (var entry in LocaleStrings.Build(language))
        {
            dictionary[entry.Key] = entry.Value;
        }

        return dictionary;
    }
}
