namespace OmniPlay.Core.Models.Entities;

public sealed class VideoFile
{
    public string Id { get; set; } = string.Empty;

    public long SourceId { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public long? MovieId { get; set; }

    public long? EpisodeId { get; set; }

    public double PlayProgress { get; set; }

    public double Duration { get; set; }
}
