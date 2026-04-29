using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Runtime;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class StoragePaths : IStoragePaths
{
    public StoragePaths()
    {
        AppRoot = AppRuntimePaths.ResolveAppRoot();

        DataDirectory = Path.Combine(AppRoot, "data");
        CacheDirectory = Path.Combine(AppRoot, "cache");
        SettingsDirectory = Path.Combine(AppRoot, "settings");
        PostersDirectory = Path.Combine(AppRoot, "posters");
        ThumbnailsDirectory = Path.Combine(AppRoot, "thumbnails");
        LogsDirectory = Path.Combine(AppRoot, "logs");
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
