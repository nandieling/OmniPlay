namespace OmniPlay.Core.Interfaces;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
}
