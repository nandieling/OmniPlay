using OmniPlay.Core.Interfaces;

namespace OmniPlay.Tests;

internal sealed class TestStoragePaths : IStoragePaths
{
    public TestStoragePaths(string rootPath)
    {
        AppRoot = rootPath;
        DataDirectory = Path.Combine(rootPath, "data");
        CacheDirectory = Path.Combine(rootPath, "cache");
        SettingsDirectory = Path.Combine(rootPath, "settings");
        PostersDirectory = Path.Combine(rootPath, "posters");
        ThumbnailsDirectory = Path.Combine(rootPath, "thumbnails");
        LogsDirectory = Path.Combine(rootPath, "logs");
    }

    public string AppRoot { get; }

    public string DataDirectory { get; }

    public string CacheDirectory { get; }

    public string SettingsDirectory { get; }

    public string PostersDirectory { get; }

    public string ThumbnailsDirectory { get; }

    public string LogsDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(PostersDirectory);
        Directory.CreateDirectory(ThumbnailsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
