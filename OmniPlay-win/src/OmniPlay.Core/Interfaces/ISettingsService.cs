using OmniPlay.Core.Settings;

namespace OmniPlay.Core.Interfaces;

public interface ISettingsService
{
    string SettingsDirectory { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
