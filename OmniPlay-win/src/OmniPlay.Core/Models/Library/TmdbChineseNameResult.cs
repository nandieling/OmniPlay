namespace OmniPlay.Core.Models.Library;

public enum TmdbChineseNameResultKind
{
    /// <summary>拿到了中文译名。</summary>
    Found = 0,

    /// <summary>TMDB 确认该条目没有中文译名（可以负缓存，短期内不再重复请求）。</summary>
    NotTranslated = 1,

    /// <summary>请求失败（网络、限流、凭证），不能当作"没有中文"缓存住。</summary>
    RequestFailed = 2
}

/// <summary>中文译名查询结果，区分"确实没有中文"和"这次没问到"。</summary>
public sealed record TmdbChineseNameResult(TmdbChineseNameResultKind Kind, string? Name)
{
    public static TmdbChineseNameResult Found(string name) => new(TmdbChineseNameResultKind.Found, name);

    public static readonly TmdbChineseNameResult NotTranslated = new(TmdbChineseNameResultKind.NotTranslated, null);

    public static readonly TmdbChineseNameResult RequestFailed = new(TmdbChineseNameResultKind.RequestFailed, null);

    public bool IsFound => Kind == TmdbChineseNameResultKind.Found && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>人物档案基础信息。</summary>
public sealed record TmdbPersonDetail(
    int Id,
    string? Name,
    string? ChineseName,
    string? Biography,
    string? Birthday,
    string? PlaceOfBirth,
    string? ProfilePath,
    System.Collections.Generic.IReadOnlyList<string> AlsoKnownAs);
