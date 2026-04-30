using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using OmniPlay.Core.Interfaces;

namespace OmniPlay.Desktop.Services;

public sealed class FolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var mainWindow = desktop.MainWindow;
        if (mainWindow?.StorageProvider is null)
        {
            return null;
        }

        var folders = await mainWindow.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择视频目录",
                AllowMultiple = false
            });

        var folder = folders.FirstOrDefault();
        if (folder?.Path is null)
        {
            return null;
        }

        return folder.Path.IsAbsoluteUri ? folder.Path.LocalPath : folder.Path.OriginalString;
    }
}
