namespace OmniPlay.Core.Models.Playback;

public sealed record PlayerSubtitleSizeOption(int Size, string Label)
{
    public override string ToString() => Label;
}
