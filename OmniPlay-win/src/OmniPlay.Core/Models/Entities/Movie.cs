namespace OmniPlay.Core.Models.Entities;

public sealed class Movie
{
    public long? Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? ReleaseDate { get; set; }

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }

    public double? VoteAverage { get; set; }

    public bool IsLocked { get; set; }

    public string? ProductionCountryCodes { get; set; }

    public string? OriginalLanguage { get; set; }

    public string? MetadataLanguage { get; set; }
}
