namespace OmniPlay.Core.Models;

public sealed record TmdbSettings(
    bool EnableMetadataEnrichment = true,
    bool EnablePosterDownloads = true,
    string CustomApiKey = "",
    string CustomAccessToken = "",
    string Language = "zh-CN");
