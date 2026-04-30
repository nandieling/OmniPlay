namespace OmniPlay.Core.Models.Playback;

public sealed class MediaPlayerOpenResult
{
    public static MediaPlayerOpenResult Success(string message) => new()
    {
        Succeeded = true,
        Message = message
    };

    public static MediaPlayerOpenResult Failure(string message) => new()
    {
        Succeeded = false,
        Message = message
    };

    public bool Succeeded { get; init; }

    public string Message { get; init; } = string.Empty;
}
