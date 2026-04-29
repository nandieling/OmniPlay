namespace OmniPlay.Core.Interfaces;

public interface ISubtitlePickerService
{
    Task<string?> PickSubtitleFileAsync(CancellationToken cancellationToken = default);
}
