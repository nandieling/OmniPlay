namespace OmniPlay.Core.Interfaces;

public interface IStoragePaths
{
    string AppRoot { get; }
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string SettingsDirectory { get; }
    string PostersDirectory { get; }
    string ThumbnailsDirectory { get; }
    string LogsDirectory { get; }

    void EnsureCreated();
}
