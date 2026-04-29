using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Core.ViewModels.Player;

namespace OmniPlay.Tests;

public sealed class PlayerViewModelTests
{
    [Fact]
    public async Task OpenAsync_WithResumePosition_SeeksAfterStartup()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv", 120);
        await Task.Delay(450);

        Assert.Equal(120, mediaPlayer.LastSeekPositionSeconds);
        Assert.Equal(120, viewModel.CurrentPositionSeconds);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OpenAsync_WithSmallResumePosition_DoesNotSeek()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv", 3);
        await Task.Delay(300);

        Assert.Null(mediaPlayer.LastSeekPositionSeconds);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OpenAsync_RefreshesVolumeFromPlaybackState()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            VolumePercent = 42
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");

        Assert.Equal(42, viewModel.VolumePercent);
        Assert.Equal("42%", viewModel.VolumeText);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OpenAsync_WithSeparateDisplayPath_UsesSafeCurrentFilePath()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);
        var request = new PlaybackOpenRequest(
            "https://demo:secret@demo.example/library/movies/Inception.mkv",
            "https://demo.example/library/movies/Inception.mkv");

        await viewModel.OpenAsync(request);

        Assert.Equal("https://demo.example/library/movies/Inception.mkv", viewModel.CurrentFilePath);
        Assert.Equal("Inception.mkv", viewModel.CurrentFileName);
        Assert.Equal("https://demo:secret@demo.example/library/movies/Inception.mkv", mediaPlayer.LastOpenedPlaybackPath);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task IncreaseVolumeCommand_ClampsToMaximum()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            VolumePercent = 95
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");
        await viewModel.IncreaseVolumeCommand.ExecuteAsync(null);

        Assert.Equal(100, mediaPlayer.LastSetVolumePercent);
        Assert.Equal(100, viewModel.VolumePercent);
        Assert.Equal("100%", viewModel.VolumeText);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task DecreaseVolumeCommand_ClampsToZero()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            VolumePercent = 5
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");
        await viewModel.DecreaseVolumeCommand.ExecuteAsync(null);

        Assert.Equal(0, mediaPlayer.LastSetVolumePercent);
        Assert.Equal(0, viewModel.VolumePercent);
        Assert.Equal("0%", viewModel.VolumeText);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OpenAsync_RefreshesAudioAndSubtitleTracks()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            AudioTracks =
            [
                new PlayerTrackInfo("audio", 1, "国语"),
                new PlayerTrackInfo("audio", 2, "英语", true)
            ],
            SubtitleTracks =
            [
                new PlayerTrackInfo("sub", 7, "简体中文", true),
                new PlayerTrackInfo("sub", 8, "English")
            ]
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");

        Assert.True(viewModel.HasAudioTrackSelection);
        Assert.True(viewModel.HasSubtitleTrackSelection);
        Assert.Equal(2, viewModel.AudioTracks.Count);
        Assert.Equal(3, viewModel.SubtitleTracks.Count);
        Assert.Equal(2, viewModel.SelectedAudioTrack?.TrackId);
        Assert.Equal(7, viewModel.SelectedSubtitleTrack?.TrackId);
        Assert.Equal("关闭字幕", viewModel.SubtitleTracks[0].DisplayName);
        Assert.True(viewModel.SubtitleTracks[0].IsOffOption);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OpenAsync_WhenNoSubtitleSelected_SelectsDefaultChineseSubtitle()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            SubtitleTracks =
            [
                new PlayerTrackInfo("sub", 3, "简中"),
                new PlayerTrackInfo("sub", 4, "英字")
            ]
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");
        await Task.Delay(50);

        Assert.NotNull(viewModel.SelectedSubtitleTrack);
        Assert.False(viewModel.SelectedSubtitleTrack!.IsOffOption);
        Assert.Equal(3, viewModel.SelectedSubtitleTrack.TrackId);
        Assert.Equal(3, mediaPlayer.LastSelectedSubtitleTrackId);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OpenAsync_AppliesConfiguredDefaultAudioAndSubtitleTracks()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            AudioTracks =
            [
                new PlayerTrackInfo("audio", 1, "中文", true, false, "chi"),
                new PlayerTrackInfo("audio", 2, "English", false, false, "eng")
            ],
            SubtitleTracks =
            [
                new PlayerTrackInfo("sub", 7, "中文", false, false, "chi"),
                new PlayerTrackInfo("sub", 8, "English", false, false, "eng")
            ]
        };
        var viewModel = new PlayerViewModel(mediaPlayer);
        viewModel.ConfigureDefaultTracks("eng", "eng");

        await viewModel.OpenAsync("sample.mkv");
        await Task.Delay(50);

        Assert.Equal(2, viewModel.SelectedAudioTrack?.TrackId);
        Assert.Equal(8, viewModel.SelectedSubtitleTrack?.TrackId);
        Assert.Equal(2, mediaPlayer.LastSelectedAudioTrackId);
        Assert.Equal(8, mediaPlayer.LastSelectedSubtitleTrackId);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task OpenAsync_SmartDefaultAudioUsesResolvedTmdbTrackMode()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            AudioTracks =
            [
                new PlayerTrackInfo("audio", 1, "英语", true, false, "eng"),
                new PlayerTrackInfo("audio", 2, "日本語", false, false, "jpn")
            ]
        };
        var viewModel = new PlayerViewModel(mediaPlayer);
        viewModel.ConfigureDefaultTracks("auto", "chi", "jpn");

        await viewModel.OpenAsync("sample.mkv");
        await Task.Delay(50);

        Assert.Equal(2, viewModel.SelectedAudioTrack?.TrackId);
        Assert.Equal(2, mediaPlayer.LastSelectedAudioTrackId);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SelectedAudioTrack_TriggersMediaPlayerSwitch()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            AudioTracks =
            [
                new PlayerTrackInfo("audio", 1, "国语", true),
                new PlayerTrackInfo("audio", 2, "英语")
            ]
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");
        viewModel.SelectedAudioTrack = viewModel.AudioTracks.Single(track => track.TrackId == 2);
        await Task.Delay(50);

        Assert.Equal(2, mediaPlayer.LastSelectedAudioTrackId);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SelectedSubtitleTrack_UsesNullTrackIdForOffOption()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            SubtitleTracks =
            [
                new PlayerTrackInfo("sub", 7, "简中", true),
                new PlayerTrackInfo("sub", 8, "英字")
            ]
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");
        viewModel.SelectedSubtitleTrack = viewModel.SubtitleTracks.Single(track => track.IsOffOption);
        await Task.Delay(50);

        Assert.Null(mediaPlayer.LastSelectedSubtitleTrackId);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task LoadExternalSubtitleCommand_LoadsSubtitleAndRefreshesTracks()
    {
        var subtitlePath = Path.Combine(Path.GetTempPath(), $"omniplay-sub-{Guid.NewGuid():N}.srt");
        await File.WriteAllTextAsync(subtitlePath, "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n");

        try
        {
            var mediaPlayer = new RecordingMediaPlayer();
            var picker = new RecordingSubtitlePickerService(subtitlePath);
            var viewModel = new PlayerViewModel(mediaPlayer, picker);

            await viewModel.OpenAsync("sample.mkv");
            await viewModel.LoadExternalSubtitleCommand.ExecuteAsync(null);

            Assert.Equal(subtitlePath, mediaPlayer.LastLoadedSubtitlePath);
            Assert.True(viewModel.HasTrackControls);
            Assert.True(viewModel.HasSubtitleTrackSelection);
            Assert.Equal(2, viewModel.SubtitleTracks.Count);
            Assert.Equal(51, viewModel.SelectedSubtitleTrack?.TrackId);
            Assert.Contains(Path.GetFileName(subtitlePath), viewModel.StatusMessage, StringComparison.Ordinal);

            await viewModel.StopAsync();
        }
        finally
        {
            if (File.Exists(subtitlePath))
            {
                File.Delete(subtitlePath);
            }
        }
    }

    [Fact]
    public async Task SubtitleDelayCommands_AdjustDelayByHalfSecond()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");
        await viewModel.IncreaseSubtitleDelayCommand.ExecuteAsync(null);
        await viewModel.DecreaseSubtitleDelayCommand.ExecuteAsync(null);

        Assert.Equal(0, mediaPlayer.LastSubtitleDelaySeconds);
        Assert.Equal("0.0s", viewModel.SubtitleDelayText);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SelectedSubtitleSizeOption_UpdatesMediaPlayerFontSize()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");
        viewModel.SelectedSubtitleSizeOption = viewModel.SubtitleSizeOptions.Single(option => option.Size == 24);
        await Task.Delay(50);

        Assert.Equal(24, mediaPlayer.LastSubtitleFontSize);
        Assert.Equal("特大", viewModel.SubtitleSizeText);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task PlayNextEpisodeCommand_NearEnd_InvokesConfiguredAction()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);
        var invocationCount = 0;

        viewModel.ConfigureNextEpisodeAction(() =>
        {
            invocationCount++;
            return Task.CompletedTask;
        });
        viewModel.DurationSeconds = 300;
        viewModel.CurrentPositionSeconds = 292;
        viewModel.IsPlaying = true;

        Assert.True(viewModel.HasNextEpisodeAction);
        Assert.True(viewModel.ShouldShowNextEpisodeAction);
        Assert.True(viewModel.PlayNextEpisodeCommand.CanExecute(null));

        await viewModel.PlayNextEpisodeCommand.ExecuteAsync(null);

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task PlayPreviousEpisodeCommand_InvokesConfiguredAction()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);
        var invocationCount = 0;

        viewModel.ConfigurePreviousEpisodeAction(() =>
        {
            invocationCount++;
            return Task.CompletedTask;
        });
        viewModel.IsPlaying = true;

        Assert.True(viewModel.HasPreviousEpisodeAction);
        Assert.True(viewModel.PlayPreviousEpisodeCommand.CanExecute(null));

        await viewModel.PlayPreviousEpisodeCommand.ExecuteAsync(null);

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task OpenAsync_TracksPlaybackCompletedState()
    {
        var mediaPlayer = new RecordingMediaPlayer
        {
            IsPlaybackCompleted = true
        };
        var viewModel = new PlayerViewModel(mediaPlayer);

        await viewModel.OpenAsync("sample.mkv");

        Assert.True(viewModel.IsPlaybackCompleted);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SelectPlaybackNavigationItemCommand_InvokesConfiguredAction()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);
        string? selectedEpisodeId = null;
        var targetItem = new PlayerNavigationItem("ep-2", "S01E02", "Episode 2", "未开始");

        viewModel.ConfigurePlaybackNavigation(
            [
                new PlayerNavigationItem("ep-1", "S01E01", "Episode 1", "当前播放", true),
                targetItem
            ],
            episodeId =>
            {
                selectedEpisodeId = episodeId;
                return Task.CompletedTask;
            });

        Assert.True(viewModel.HasPlaybackNavigation);
        Assert.True(viewModel.SelectPlaybackNavigationItemCommand.CanExecute(targetItem));

        await viewModel.SelectPlaybackNavigationItemCommand.ExecuteAsync(targetItem);

        Assert.Equal("ep-2", selectedEpisodeId);
    }

    [Fact]
    public async Task SelectedPlaybackNavigationSeason_InvokesConfiguredAction()
    {
        var mediaPlayer = new RecordingMediaPlayer();
        var viewModel = new PlayerViewModel(mediaPlayer);
        var selectedSeason = 0;

        viewModel.ConfigurePlaybackNavigation(
            [new PlayerNavigationItem("ep-1", "S01E01", "Episode 1", "当前播放", true)],
            _ => Task.CompletedTask,
            seasons: [1, 2],
            selectedSeason: 1,
            seasonAction: season =>
            {
                selectedSeason = season;
                return Task.CompletedTask;
            });

        Assert.True(viewModel.HasPlaybackNavigationSeasons);

        viewModel.SelectedPlaybackNavigationSeason = 2;
        await Task.Delay(20);

        Assert.Equal(2, selectedSeason);
    }

    private sealed class RecordingMediaPlayer : IMediaPlayer
    {
        public bool IsAvailable => true;

        public string BackendName => "fake";

        public double? LastSeekPositionSeconds { get; private set; }

        public double? LastSetVolumePercent { get; private set; }

        public long? LastSelectedAudioTrackId { get; private set; }

        public long? LastSelectedSubtitleTrackId { get; private set; }

        public string? LastLoadedSubtitlePath { get; private set; }

        public string? LastOpenedPlaybackPath { get; private set; }

        public double? LastSubtitleDelaySeconds { get; private set; }

        public int? LastSubtitleFontSize { get; private set; }

        public double VolumePercent { get; set; } = 100;

        public double SubtitleDelaySeconds { get; set; }

        public int SubtitleFontSize { get; set; } = 16;

        public bool IsPlaybackCompleted { get; set; }

        public IReadOnlyList<PlayerTrackInfo> AudioTracks { get; set; } = [];

        public IReadOnlyList<PlayerTrackInfo> SubtitleTracks { get; set; } = [];

        public void Initialize()
        {
        }

        public void AttachToHost(IntPtr hostHandle)
        {
        }

        public Task<MediaPlayerOpenResult> OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default)
        {
            LastOpenedPlaybackPath = request.PlaybackPath;
            return Task.FromResult(MediaPlayerOpenResult.Success("opened"));
        }

        public Task<PlayerPlaybackState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PlayerPlaybackState
            {
                HasMedia = true,
                PositionSeconds = LastSeekPositionSeconds ?? 0,
                DurationSeconds = 300,
                IsPlaybackCompleted = IsPlaybackCompleted,
                VolumePercent = VolumePercent,
                SubtitleDelaySeconds = SubtitleDelaySeconds,
                SubtitleFontSize = SubtitleFontSize,
                AudioTracks = AudioTracks,
                SubtitleTracks = SubtitleTracks
            });
        }

        public Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SeekAsync(double positionSeconds, CancellationToken cancellationToken = default)
        {
            LastSeekPositionSeconds = positionSeconds;
            return Task.CompletedTask;
        }

        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default)
        {
            LastSetVolumePercent = volumePercent;
            VolumePercent = volumePercent;
            return Task.CompletedTask;
        }

        public Task SelectAudioTrackAsync(long? trackId, CancellationToken cancellationToken = default)
        {
            LastSelectedAudioTrackId = trackId;
            AudioTracks = AudioTracks
                .Select(track => track with { IsSelected = track.TrackId == trackId })
                .ToArray();
            return Task.CompletedTask;
        }

        public Task SelectSubtitleTrackAsync(long? trackId, CancellationToken cancellationToken = default)
        {
            LastSelectedSubtitleTrackId = trackId;
            SubtitleTracks = SubtitleTracks
                .Select(track => track with { IsSelected = track.TrackId == trackId })
                .ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> LoadExternalSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default)
        {
            LastLoadedSubtitlePath = subtitlePath;
            SubtitleTracks =
            [
                new PlayerTrackInfo("sub", 51, Path.GetFileName(subtitlePath), true)
            ];
            return Task.FromResult(true);
        }

        public Task SetSubtitleDelayAsync(double delaySeconds, CancellationToken cancellationToken = default)
        {
            LastSubtitleDelaySeconds = delaySeconds;
            SubtitleDelaySeconds = delaySeconds;
            return Task.CompletedTask;
        }

        public Task SetSubtitleFontSizeAsync(int fontSize, CancellationToken cancellationToken = default)
        {
            LastSubtitleFontSize = fontSize;
            SubtitleFontSize = fontSize;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSubtitlePickerService : ISubtitlePickerService
    {
        private readonly string? path;

        public RecordingSubtitlePickerService(string? path)
        {
            this.path = path;
        }

        public Task<string?> PickSubtitleFileAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(path);
        }
    }
}
