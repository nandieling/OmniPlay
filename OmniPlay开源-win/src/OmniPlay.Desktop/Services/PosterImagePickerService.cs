using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using OmniPlay.Core.Interfaces;

namespace OmniPlay.Desktop.Services;

public sealed class PosterImagePickerService : IPosterImagePickerService
{
    private static readonly FilePickerFileType PosterImageFileType = new("海报图片")
    {
        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp"]
    };

    private readonly IStoragePaths storagePaths;

    public PosterImagePickerService(IStoragePaths storagePaths)
    {
        this.storagePaths = storagePaths;
    }

    public async Task<string?> PickPosterImageAsync(CancellationToken cancellationToken = default)
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
                Title = "选择海报图片",
                AllowMultiple = false,
                FileTypeFilter = [PosterImageFileType]
            });

        var file = files.FirstOrDefault();
        if (file?.Path is null)
        {
            return null;
        }

        var sourcePath = file.Path.IsAbsoluteUri ? file.Path.LocalPath : file.Path.OriginalString;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        Directory.CreateDirectory(storagePaths.PostersDirectory);
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        var targetPath = Path.Combine(storagePaths.PostersDirectory, $"custom-poster-{Guid.NewGuid():N}{extension}");
        File.Copy(sourcePath, targetPath);
        return targetPath;
    }
}
