namespace OmniPlay.Core.Settings;

public sealed record TmdbSettings
{
    public const string DefaultLanguage = "zh-CN";

    public bool EnableMetadataEnrichment { get; init; } = true;

    public bool EnablePosterDownloads { get; init; } = true;

    public bool EnableEpisodeThumbnailDownloads { get; init; } = true;

    public bool EnableBuiltInPublicSource { get; init; } = true;

    public string CustomApiKey { get; init; } = string.Empty;

    public string CustomAccessToken { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;
}
