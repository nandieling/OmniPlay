using System.Net;
using System.Net.Security;
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
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                UseCookies = false
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
        });
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IMediaSourceRepository, MediaSourceRepository>();
        services.AddSingleton<IMovieRepository, MovieRepository>();
        services.AddSingleton<ITvShowRepository, TvShowRepository>();
        services.AddSingleton<IVideoFileRepository, VideoFileRepository>();
        services.AddSingleton<WebDavDiscoveryClient>();
        services.AddSingleton<IWebDavDiscoveryClient>(provider => provider.GetRequiredService<WebDavDiscoveryClient>());
        services.AddSingleton<IWebDavConnectionTester>(provider => provider.GetRequiredService<WebDavDiscoveryClient>());
        services.AddSingleton<INetworkShareDiscoveryService, NetworkShareDiscoveryService>();
        services.AddSingleton<ITmdbMetadataClient, TmdbMetadataClient>();
        services.AddSingleton<ITmdbConnectionTester>(provider => (TmdbMetadataClient)provider.GetRequiredService<ITmdbMetadataClient>());
        services.AddSingleton<ILibraryMetadataEditor, LibraryMetadataEditor>();
        services.AddSingleton<ILibraryMetadataEnricher, LibraryMetadataEnricher>();
        services.AddSingleton<ILibraryThumbnailEnricher, LibraryThumbnailEnricher>();
        services.AddSingleton<ILibraryScanner, LibraryScanner>();
        services.AddSingleton<IMediaPlayer, MpvPlayer>();
        services.AddSingleton<IPlaybackLauncher, ShellPlaybackLauncher>();
        services.AddSingleton<IExternalLinkOpener, ShellExternalLinkOpener>();

        return services.BuildServiceProvider();
    }
}
