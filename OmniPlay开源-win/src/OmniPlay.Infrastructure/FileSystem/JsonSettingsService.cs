using System.Text.Json;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Settings;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim syncLock = new(1, 1);
    private AppSettings? cachedSettings;

    public JsonSettingsService(IStoragePaths storagePaths)
    {
        SettingsDirectory = storagePaths.SettingsDirectory;
        SettingsFilePath = Path.Combine(SettingsDirectory, "appsettings.json");
    }

    public string SettingsDirectory { get; }

    private string SettingsFilePath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await syncLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedSettings is not null)
            {
                return cachedSettings;
            }

            Directory.CreateDirectory(SettingsDirectory);

            if (!File.Exists(SettingsFilePath))
            {
                cachedSettings = new AppSettings();
                return cachedSettings;
            }

            await using var stream = File.OpenRead(SettingsFilePath);
            cachedSettings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                SerializerOptions,
                cancellationToken) ?? new AppSettings();
            return cachedSettings;
        }
        finally
        {
            syncLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await syncLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var tempPath = SettingsFilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
            }

            if (File.Exists(SettingsFilePath))
            {
                File.Delete(SettingsFilePath);
            }

            File.Move(tempPath, SettingsFilePath);
            cachedSettings = settings;
        }
        finally
        {
            syncLock.Release();
        }
    }
}
