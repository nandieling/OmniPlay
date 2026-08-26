using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.DependencyInjection;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.ViewModels;
using OmniPlay.Core.ViewModels.Library;
using OmniPlay.Core.ViewModels.Player;
using OmniPlay.Core.ViewModels.Settings;
using OmniPlay.Desktop.Services;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using OmniPlay.Infrastructure.Thumbnails;
using OmniPlay.Infrastructure.Tmdb;
using OmniPlay.Player.Mpv;

namespace OmniPlay.Desktop.Bootstrap;

public static class ServiceRegistration
{
    public static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<PosterWallViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<StandalonePlayerWindowManager>();

        services.AddSingleton<IStoragePaths, StoragePaths>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddSingleton<IPosterImagePickerService, PosterImagePickerService>();
        services.AddSingleton<ISubtitlePickerService, SubtitlePickerService>();
        services.AddSingleton(_ =>
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                UseCookies = false
            };
            handler.ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == SslPolicyErrors.None ||
                IsLocalOrPrivateHost(request.RequestUri?.Host);
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
        });
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IMediaSourceRepository, MediaSourceRepository>();
        services.AddSingleton<INetworkCredentialStore, NetworkCredentialStore>();
        services.AddSingleton<IMovieRepository, MovieRepository>();
        services.AddSingleton<ITvShowRepository, TvShowRepository>();
        services.AddSingleton<IVideoFileRepository, VideoFileRepository>();
        services.AddSingleton<WebDavDiscoveryClient>();
        services.AddSingleton<IWebDavDiscoveryClient>(provider => provider.GetRequiredService<WebDavDiscoveryClient>());
        services.AddSingleton<IWebDavConnectionTester>(provider => provider.GetRequiredService<WebDavDiscoveryClient>());
        services.AddSingleton<INetworkShareDiscoveryService, NetworkShareDiscoveryService>();
        services.AddSingleton<IMediaServerDiscoveryClient, MediaServerDiscoveryClient>();
        services.AddSingleton<IMediaServerPreflightService, MediaServerPreflightService>();
        services.AddSingleton<IOmniPlayDockerClient, OmniPlayDockerClient>();
        services.AddSingleton<ILuckyStunClient, LuckyStunClient>();
        services.AddSingleton<IDoubanMetadataService, DoubanMetadataService>();
        services.AddSingleton<ITmdbMetadataClient, TmdbMetadataClient>();
        services.AddSingleton<ITmdbConnectionTester>(provider => (TmdbMetadataClient)provider.GetRequiredService<ITmdbMetadataClient>());
        services.AddSingleton<ILibraryMetadataEditor, LibraryMetadataEditor>();
        services.AddSingleton<ILocalMetadataSidecarService, LocalMetadataSidecarService>();
        services.AddSingleton<ILibraryMetadataEnricher, LibraryMetadataEnricher>();
        services.AddSingleton<ILibraryThumbnailEnricher, LibraryThumbnailEnricher>();
        services.AddSingleton<ILibraryScanner, LibraryScanner>();
        services.AddSingleton<IMediaPlayer, MpvPlayer>();
        services.AddSingleton<IPlaybackLauncher, ShellPlaybackLauncher>();
        services.AddSingleton<IExternalLinkOpener, ShellExternalLinkOpener>();

        return services.BuildServiceProvider();
    }

    private static bool IsLocalOrPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().Trim('[', ']').ToLowerInvariant();
        if (normalized == "localhost" || normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(normalized, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        }

        return false;
    }
}
