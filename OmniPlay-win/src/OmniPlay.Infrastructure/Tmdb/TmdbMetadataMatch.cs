namespace OmniPlay.Infrastructure.Tmdb;

public sealed record TmdbMetadataMatch(
    int Id,
    string MediaType,
    string Title,
    string? Overview,
    string? ReleaseDate,
    string? FirstAirDate,
    string? PosterPath,
    double? VoteAverage,
    double? Popularity,
    string? OriginalTitle = null,
    string? ProductionCountryCodes = null,
    string? OriginalLanguage = null);
