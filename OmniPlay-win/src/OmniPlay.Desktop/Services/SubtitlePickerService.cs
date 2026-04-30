using System.Linq;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using OmniPlay.Core.Interfaces;

namespace OmniPlay.Desktop.Services;

public sealed class SubtitlePickerService : ISubtitlePickerService
{
    private static readonly FilePickerFileType SubtitleFileType = new("字幕文件")
    {
        Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub", "*.idx"]
    };

    public async Task<string?> PickSubtitleFileAsync(CancellationToken cancellationToken = default)
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

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择外挂字幕",
                AllowMultiple = false,
                FileTypeFilter = [SubtitleFileType]
            });

        var file = files.FirstOrDefault();
        if (file?.Path is null)
        {
            return null;
        }

        return file.Path.IsAbsoluteUri ? file.Path.LocalPath : file.Path.OriginalString;
    }
}
