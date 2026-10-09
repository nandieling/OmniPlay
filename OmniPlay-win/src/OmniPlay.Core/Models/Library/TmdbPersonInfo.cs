namespace OmniPlay.Core.Models.Library;

/// <summary>
/// 一部作品的导演/主演名单。列表是权威数据（全量名单 + 人物 id），
/// <see cref="Director"/>/<see cref="MainCast"/> 保留历史扁平字符串形式，
/// 供旧数据列与既有展示逻辑直接使用。
/// </summary>
public sealed record TmdbCredits(
    System.Collections.Generic.IReadOnlyList<OmniPlay.Core.Models.Library.TmdbPersonInfo> Directors,
    System.Collections.Generic.IReadOnlyList<OmniPlay.Core.Models.Library.TmdbPersonInfo> Casts)
{
    /// <summary>导演扁平串（顿号分隔），无名单时为 null。</summary>
    public string? Director => Directors.Count == 0
        ? null
        : string.Join("\u3001", System.Linq.Enumerable.Select(Directors, static person => person.Name));

    /// <summary>主演扁平串（顿号分隔），无名单时为 null。</summary>
    public string? MainCast => Casts.Count == 0
        ? null
        : string.Join("\u3001", System.Linq.Enumerable.Select(Casts, static person => person.Name));
}


/// <summary>TMDB 演职员条目（含人物 id 与头像路径，用于人物档案与中文译名查询）。</summary>
public sealed record TmdbPersonInfo(int Id, string Name, string? ProfilePath = null);

/// <summary>人物作品条目，来自 <c>/person/{id}/combined_credits</c>。</summary>
public sealed class TmdbFilmographyItem
{
    public string Title { get; set; } = string.Empty;

    public double? Rating { get; set; }

    public int? Year { get; set; }

    public string? Date { get; set; }

    public int? TmdbId { get; set; }

    /// <summary>作品类型（movie/tv）。老缓存里没有该字段，缺中文片名时按两种类型探测。</summary>
    public string? MediaType { get; set; }
}
