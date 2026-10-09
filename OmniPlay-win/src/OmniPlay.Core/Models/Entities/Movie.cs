namespace OmniPlay.Core.Models.Entities;

public sealed class Movie
{
    public long? Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? ReleaseDate { get; set; }

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }

    public double? VoteAverage { get; set; }

    public double? DoubanRating { get; set; }

    public bool IsLocked { get; set; }

    public string? ProductionCountryCodes { get; set; }

    public string? OriginalLanguage { get; set; }

    /// <summary>导演全量名单（JSON 数组）；<see cref="Director"/> 保留其扁平形式供旧 UI 与历史数据兼容。</summary>
    public string? Directors { get; set; }

    /// <summary>主演全量名单（JSON 数组）。</summary>
    public string? Casts { get; set; }

    public System.Collections.Generic.List<string> DirectorList => OmniPlay.Core.Models.PersonNames.Decode(Directors ?? Director);

    public System.Collections.Generic.List<string> CastList => OmniPlay.Core.Models.PersonNames.Decode(Casts ?? MainCast);

    public string? Director { get; set; }

    public string? MainCast { get; set; }

    public string? MetadataLanguage { get; set; }

    public int? TmdbId { get; set; }
}
