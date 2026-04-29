namespace OmniPlay.Core.Interfaces;

public interface IPosterImagePickerService
{
    Task<string?> PickPosterImageAsync(CancellationToken cancellationToken = default);
}
