using OmniPlay.Core.Settings;

namespace OmniPlay.Infrastructure.Tmdb;

public sealed record TmdbSearchOptions
{
    public int? PreferredSeason { get; init; }

    public string? SecondaryQuery { get; init; }

    public TmdbSettings? SettingsOverride { get; init; }
}
