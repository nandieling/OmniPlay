using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Library;

namespace OmniPlay.Infrastructure.Library;

/// <summary>导演/主演列的写入形态:JSON 数组列为准,旧的分隔符文本列作为投影保留。</summary>
public readonly record struct PeopleColumns(
    string? Directors,
    string? Casts,
    string? DirectorText,
    string? MainCastText)
{
    /// <summary>手动编辑框里的自由文本:同样按分隔符拆成名单后落库。</summary>
    public static string? EncodeEdited(string? value) => PersonNames.Encode(PersonNames.Split(value));

    public static PeopleColumns FromLegacyText(string? director, string? mainCast) =>
        FromTexts(director, mainCast, director, mainCast);

    public static PeopleColumns FromTexts(
        string? director,
        string? mainCast,
        string? fallbackDirectorText,
        string? fallbackMainCastText)
    {
        var directors = PersonNames.Encode(PersonNames.Split(director));
        var casts = PersonNames.Encode(PersonNames.Split(mainCast));
        return new PeopleColumns(
            directors,
            casts,
            PersonNames.Flatten(directors) ?? fallbackDirectorText,
            PersonNames.Flatten(casts) ?? fallbackMainCastText);
    }

    public static PeopleColumns FromCredits(TmdbCredits? credits, string? legacyDirector, string? legacyCast)
    {
        if (credits is null)
        {
            return FromLegacyText(legacyDirector, legacyCast);
        }

        var directors = credits.Directors.Count > 0
            ? PersonNames.Encode(credits.Directors.Select(person => person.Name).ToList())
            : PersonNames.Encode(PersonNames.Split(legacyDirector));
        var casts = credits.Casts.Count > 0
            ? PersonNames.Encode(credits.Casts.Select(person => person.Name).ToList())
            : PersonNames.Encode(PersonNames.Split(legacyCast));
        return new PeopleColumns(
            directors,
            casts,
            PersonNames.Flatten(directors) ?? legacyDirector,
            PersonNames.Flatten(casts) ?? legacyCast);
    }
}

/// <summary>刮削完成后统一走一遍中文译名覆盖,并把本片人物登记进导演/主演档案。</summary>
internal static class PeopleColumnFactory
{
    internal static async Task<TmdbCredits?> RefreshPeopleAsync(
        ICategoryFolderStore? categoryFolderStore,
        TmdbCredits? credits,
        CancellationToken cancellationToken)
    {
        if (categoryFolderStore is null || credits is null)
        {
            return credits;
        }

        var localized = await categoryFolderStore.LocalizeCreditsAsync(credits, cancellationToken);
        await categoryFolderStore.RegisterCreditsAsync(localized ?? credits, cancellationToken);
        return localized ?? credits;
    }
}
