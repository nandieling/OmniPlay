using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Models.Network;
using OmniPlay.Core.Models.Playback;
using OmniPlay.Core.Settings;
using OmniPlay.Core.ViewModels.Library;
using OmniPlay.Core.ViewModels.Player;
using OmniPlay.Core.ViewModels.Settings;
using System.Globalization;

namespace OmniPlay.Tests;

public sealed class PosterWallViewModelTests
{
    [Fact]
    public async Task OpenStandalonePrimaryCommand_ClosesOverlayAndLaunchesStandaloneWindow()
    {
        var filePath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        var playbackLauncher = new FakePlaybackLauncher();
        var player = new PlayerViewModel(new FakeMediaPlayer());
        var settings = new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester());
        var viewModel = CreateViewModel(videoRepository, playbackLauncher, settings, player);
        var video = CreateVideoItem(filePath);

        viewModel.DetailPrimaryFile = video;
        player.CurrentPositionSeconds = 95;
        player.DurationSeconds = 300;

        await viewModel.PlayVideoCommand.ExecuteAsync(video);
        await viewModel.OpenStandalonePrimaryCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsPlayerOverlayOpen);
        Assert.Equal(filePath, playbackLauncher.LastOpenedFilePath);
        Assert.Equal(video.Id, videoRepository.LastUpdatedVideoId);
        Assert.Equal(95, videoRepository.LastUpdatedProgressSeconds);
        Assert.Equal(300, videoRepository.LastUpdatedDurationSeconds);
    }

    [Fact]
    public async Task PlayVideoCommand_SetsPendingResumePosition_ForInProgressFile()
    {
        var filePath = CreateMediaFile();
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var video = CreateVideoItem(filePath, playProgress: 88, duration: 300);

        await viewModel.PlayVideoCommand.ExecuteAsync(video);

        Assert.True(viewModel.IsPlayerOverlayOpen);
        Assert.Equal(88, viewModel.PendingPlaybackStartPositionSeconds);
        Assert.Contains("继续播放", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenStandalonePrimaryCommand_PassesResumePositionToLauncher()
    {
        var filePath = CreateMediaFile();
        var playbackLauncher = new FakePlaybackLauncher();
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            playbackLauncher,
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var video = CreateVideoItem(filePath, playProgress: 120, duration: 300);

        viewModel.DetailPrimaryFile = video;
        await viewModel.OpenStandalonePrimaryCommand.ExecuteAsync(null);

        Assert.Equal(filePath, playbackLauncher.LastOpenedFilePath);
        Assert.Equal(120, playbackLauncher.LastStartPositionSeconds);
    }

    [Fact]
    public async Task PlayVideoCommand_AllowsRemoteHttpUrl()
    {
        var remoteUrl = "https://demo.example/library/movies/Inception.2010.mkv";
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var video = CreateVideoItem(remoteUrl);

        await viewModel.PlayVideoCommand.ExecuteAsync(video);

        Assert.True(viewModel.IsPlayerOverlayOpen);
        Assert.Equal(remoteUrl, viewModel.PendingPlaybackFilePath);
    }

    [Fact]
    public async Task OpenStandalonePrimaryCommand_AllowsRemoteHttpUrl()
    {
        var remoteUrl = "https://demo.example/library/movies/Inception.2010.mkv";
        var playbackLauncher = new FakePlaybackLauncher();
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            playbackLauncher,
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));

        viewModel.DetailPrimaryFile = CreateVideoItem(remoteUrl, duration: 0);
        await viewModel.OpenStandalonePrimaryCommand.ExecuteAsync(null);

        Assert.Equal(remoteUrl, playbackLauncher.LastOpenedFilePath);
    }

    [Fact]
    public async Task PlayVideoCommand_DoesNotResumeWatchedFile()
    {
        var filePath = CreateMediaFile();
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var video = CreateVideoItem(filePath, playProgress: 295, duration: 300);

        await viewModel.PlayVideoCommand.ExecuteAsync(video);

        Assert.Equal(0, viewModel.PendingPlaybackStartPositionSeconds);
        Assert.DoesNotContain("继续播放", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleSortDirectionCommand_SortsRatingAscendingAndDescending()
    {
        var movieRepository = new FakeMovieRepository();
        movieRepository.Movies.AddRange(
        [
            new Movie { Id = 1, Title = "Low Rating", VoteAverage = 6.2 },
            new Movie { Id = 2, Title = "High Rating", VoteAverage = 9.1 },
            new Movie { Id = 3, Title = "No Rating" }
        ]);
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            movieRepository: movieRepository);

        await viewModel.LoadAsync();
        viewModel.SelectSortOptionCommand.Execute(
            viewModel.SortOptions.Single(option => option.Value == LibrarySortOption.Rating));

        Assert.Equal("升序", viewModel.SortDirectionLabel);
        Assert.True(viewModel.IsSortAscending);
        Assert.Equal(["Low Rating", "High Rating", "No Rating"], viewModel.LibraryItems.Select(item => item.Title).ToArray());

        viewModel.ToggleSortDirectionCommand.Execute(null);

        Assert.Equal("降序", viewModel.SortDirectionLabel);
        Assert.False(viewModel.IsSortAscending);
        Assert.Equal(["High Rating", "Low Rating", "No Rating"], viewModel.LibraryItems.Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task OpenStandalonePrimaryCommand_PersistsPlaybackStateWhenWindowCloses()
    {
        var filePath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        var playbackLauncher = new FakePlaybackLauncher();
        var player = new PlayerViewModel(new FakeMediaPlayer());
        var settings = new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester());
        var viewModel = CreateViewModel(videoRepository, playbackLauncher, settings, player);
        var video = CreateVideoItem(filePath);

        viewModel.DetailPrimaryFile = video;

        await viewModel.OpenStandalonePrimaryCommand.ExecuteAsync(null);
        await playbackLauncher.CompleteAsync(new PlaybackCloseResult(filePath, 42, 123));

        Assert.Equal(video.Id, videoRepository.LastUpdatedVideoId);
        Assert.Equal(42, videoRepository.LastUpdatedProgressSeconds);
        Assert.Equal(123, videoRepository.LastUpdatedDurationSeconds);
        Assert.Equal(1, videoRepository.GetContinueWatchingCallCount);
        Assert.Contains("独立窗口播放进度", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayNextEpisodeCommand_SwitchesToNextEpisodeAndMarksCurrentEpisodeWatched()
    {
        var episodeOnePath = CreateMediaFile();
        var episodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        /*
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                playProgress: 120,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "第 1 集"),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "第 2 集")
        ];

        */
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                playProgress: 120,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "E01"),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "E02")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var player = new PlayerViewModel(new FakeMediaPlayer());
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            player,
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);
        Assert.Equal("ep-1", viewModel.DetailPrimaryFile?.Id);

        await viewModel.PlayVideoCommand.ExecuteAsync(viewModel.DetailPrimaryFile);

        player.DurationSeconds = 300;
        player.CurrentPositionSeconds = 292;
        player.IsPlaying = true;

        Assert.True(player.HasNextEpisodeAction);
        Assert.True(player.ShouldShowNextEpisodeAction);

        await player.PlayNextEpisodeCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsPlayerOverlayOpen);
        Assert.Equal(episodeTwoPath, viewModel.PendingPlaybackFilePath);
        Assert.Equal("ep-2", viewModel.DetailPrimaryFile?.Id);

        var updatedEpisodeOne = videoRepository.FindVideo("ep-1");
        Assert.NotNull(updatedEpisodeOne);
        Assert.Equal(300, updatedEpisodeOne!.PlayProgress);
        Assert.Equal(300, updatedEpisodeOne.Duration);
        Assert.Equal("ep-1", videoRepository.LastUpdatedVideoId);
    }

    [Fact]
    public async Task OpenDetailCommand_SummarizesTvShowWatchStateAcrossAllEpisodes()
    {
        var episodeOnePath = CreateMediaFile();
        var episodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                playProgress: 292,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "E01"),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "E02")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);

        Assert.Equal("ep-2", viewModel.DetailPrimaryFile?.Id);
        Assert.Contains("\u672A\u770B\u5B8C", viewModel.DetailProgressSummary, StringComparison.Ordinal);
        Assert.InRange(viewModel.DetailPrimaryProgressRatio, 0.49, 0.51);

        await viewModel.ToggleDetailWatchedCommand.ExecuteAsync(viewModel.DetailPrimaryFile);

        Assert.Equal("\u5DF2\u770B", viewModel.DetailProgressSummary);
        Assert.Equal(1, viewModel.DetailPrimaryProgressRatio);
    }

    [Fact]
    public async Task PosterWatchStateConfirmationCommand_MarksWholeTvShowWatched()
    {
        var episodeOnePath = CreateMediaFile();
        var episodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                playProgress: 120,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2)
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        viewModel.OpenPosterWatchStateConfirmationCommand.Execute(posterItem);

        Assert.True(viewModel.IsPosterWatchStateConfirmationOpen);
        Assert.Contains("\u5DF2\u770B", viewModel.PosterWatchStateConfirmationActionText, StringComparison.Ordinal);
        Assert.False(videoRepository.FindVideo("ep-2")!.IsWatched);

        await viewModel.ConfirmPosterWatchStateCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsPosterWatchStateConfirmationOpen);
        Assert.True(videoRepository.FindVideo("ep-1")!.IsWatched);
        Assert.True(videoRepository.FindVideo("ep-2")!.IsWatched);
        Assert.Equal(PlaybackWatchState.Watched, Assert.Single(viewModel.LibraryItems).WatchState);
    }

    [Fact]
    public async Task PosterWatchStateConfirmationCommand_MarksWholeMovieUnwatched()
    {
        var moviePath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.MovieFilesById[9] =
        [
            CreateVideoItem(
                moviePath,
                id: "movie-file",
                playProgress: 300,
                duration: 300)
        ];

        var movieRepository = new FakeMovieRepository();
        movieRepository.Movies.Add(new Movie
        {
            Id = 9,
            Title = "Demo Movie"
        });

        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            movieRepository: movieRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);
        Assert.Equal(PlaybackWatchState.Watched, posterItem.WatchState);

        viewModel.OpenPosterWatchStateConfirmationCommand.Execute(posterItem);
        await viewModel.ConfirmPosterWatchStateCommand.ExecuteAsync(null);

        var updatedMovie = videoRepository.FindVideo("movie-file");
        Assert.NotNull(updatedMovie);
        Assert.Equal(0, updatedMovie!.PlayProgress);
        Assert.False(updatedMovie.IsWatched);
        Assert.Equal(PlaybackWatchState.Unwatched, Assert.Single(viewModel.LibraryItems).WatchState);
    }

    [Fact]
    public async Task LoadAsync_HidesPlaceholderYearUntilMetadataExists()
    {
        var movieRepository = new FakeMovieRepository();
        movieRepository.Movies.AddRange(
        [
            new Movie
            {
                Id = 1,
                Title = "Unmatched Movie",
                ReleaseDate = "2010"
            },
            new Movie
            {
                Id = 2,
                Title = "Scraped Movie",
                ReleaseDate = "2019-05-30",
                Overview = "Matched overview."
            }
        ]);
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            movieRepository: movieRepository);

        await viewModel.LoadAsync();

        var unmatched = Assert.Single(viewModel.LibraryItems, item => item.Title == "Unmatched Movie");
        var scraped = Assert.Single(viewModel.LibraryItems, item => item.Title == "Scraped Movie");
        Assert.Equal(string.Empty, unmatched.Subtitle);
        Assert.Equal("2019", scraped.Subtitle);
    }

    [Fact]
    public async Task ApplyDetailMetadataCandidateCommand_ScrapesTvShowThumbnails()
    {
        var episodePath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodePath,
                id: "ep-1",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "E01")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var metadataEditor = new FakeLibraryMetadataEditor
        {
            ApplyTvShowResult = new LibraryMetadataRefreshResult(Updated: true, Message: "applied")
        };
        var thumbnailEnricher = new FakeLibraryThumbnailEnricher
        {
            Summary = new LibraryThumbnailEnrichmentSummary(DownloadedThumbnailCount: 1)
        };
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            tvShowRepository: tvShowRepository,
            metadataEditor: metadataEditor,
            thumbnailEnricher: thumbnailEnricher);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);
        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);
        var candidate = new LibraryMetadataSearchCandidate(
            123,
            "tv",
            "Demo Show",
            null,
            null,
            "2020-01-01",
            null,
            null,
            null,
            null);

        await viewModel.ApplyDetailMetadataCandidateCommand.ExecuteAsync(candidate);

        Assert.Equal(1, metadataEditor.ApplyTvShowCallCount);
        Assert.Equal(1, thumbnailEnricher.TvShowCallCount);
        Assert.Equal(7, thumbnailEnricher.LastTvShowId);
        Assert.True(thumbnailEnricher.LastSettings?.EnableEpisodeThumbnailDownloads);
        Assert.True(videoRepository.GetByTvShowCallCount >= 2);
        Assert.Contains("1", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("分集剧照", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.True(viewModel.HasStatusMessage);
    }

    [Fact]
    public async Task OpenDetailCommand_ForMovie_DoesNotExposeSeriesSeasonState()
    {
        var moviePath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.MovieFilesById[9] =
        [
            CreateVideoItem(moviePath, id: "movie-file")
        ];

        var movieRepository = new FakeMovieRepository();
        movieRepository.Movies.Add(new Movie
        {
            Id = 9,
            Title = "Demo Movie"
        });

        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            movieRepository: movieRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);

        Assert.False(viewModel.IsDetailSeries);
        Assert.False(viewModel.HasSeasonTabs);
        Assert.Empty(viewModel.AvailableSeasons);
        Assert.Equal("文件列表", viewModel.DetailCollectionHeading);
        Assert.False(Assert.Single(viewModel.DetailFiles).IsTvEpisode);
    }

    [Fact]
    public void OpenEpisodeEditCommand_LeavesParsedSubtitleEmptyByDefault()
    {
        var video = new LibraryVideoItem
        {
            Id = "ep-1",
            FileName = "Show.S01E01.Topic.mkv",
            AbsolutePath = CreateMediaFile(),
            PlaybackPath = CreateMediaFile(),
            IsTvEpisode = true,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            EpisodeSubtitle = "Topic"
        };
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        viewModel.IsDetailSeries = true;

        viewModel.OpenEpisodeEditCommand.Execute(video);

        Assert.Equal(string.Empty, viewModel.EpisodeEditSubtitle);
    }

    [Fact]
    public async Task OpenPosterScrapeAndEditCommands_ShowDecodedSourceRelativePath()
    {
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.MovieFilesById[9] =
        [
            new LibraryVideoItem
            {
                Id = "remote-movie",
                FileName = "%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202.mkv",
                RelativePath = "movies/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202.mkv",
                AbsolutePath = "https://demo.example:5005/library/movies/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202.mkv",
                PlaybackPath = "https://demo.example/library/movies/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83%202.mkv"
            }
        ];
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var poster = new LibraryPosterItem
        {
            Id = "movie-9",
            Title = "流浪地球 2",
            MediaKind = "电影",
            MovieId = 9
        };

        await viewModel.OpenPosterScrapeCommand.ExecuteAsync(poster);

        Assert.Equal("movies/流浪地球 2/流浪地球 2.mkv", viewModel.PosterSourceFileText);
        viewModel.ClosePosterScrapeCommand.Execute(null);

        await viewModel.OpenPosterEditCommand.ExecuteAsync(poster);

        Assert.Equal("movies/流浪地球 2/流浪地球 2.mkv", viewModel.PosterSourceFileText);
    }

    [Fact]
    public async Task OpenPosterScrapeCommand_UsesSourceTitleBeforeYearForDefaultQuery()
    {
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.MovieFilesById[9] =
        [
            new LibraryVideoItem
            {
                Id = "american-beauty",
                FileName = "American.Beauty.1999.Paramount.Blu-ray.1080p.AVC.DTS-HD.MA.5.1@blucook#262.iso",
                RelativePath = "American Beauty 1999 Paramount Blu-ray 1080p AVC DTS-HD MA 5.1-blucook#262@CHDBits/American.Beauty.1999.Paramount.Blu-ray.1080p.AVC.DTS-HD.MA.5.1@blucook#262.iso",
                AbsolutePath = "https://demo.example/library/American.Beauty.1999.Paramount.Blu-ray.1080p.AVC.DTS-HD.MA.5.1@blucook#262.iso",
                PlaybackPath = "https://demo.example/library/American.Beauty.1999.Paramount.Blu-ray.1080p.AVC.DTS-HD.MA.5.1@blucook#262.iso"
            }
        ];
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var poster = new LibraryPosterItem
        {
            Id = "movie-9",
            Title = "American Beauty Paramount",
            Subtitle = "1999",
            MediaKind = "电影",
            MovieId = 9
        };

        await viewModel.OpenPosterScrapeCommand.ExecuteAsync(poster);

        Assert.Equal("American Beauty", viewModel.PosterScrapeQuery);
        Assert.Equal("1999", viewModel.PosterScrapeYear);
    }

    [Fact]
    public async Task OpenPosterScrapeCommand_RemovesDiscPrefixDashAndTheMovieSuffixFromDefaultQuery()
    {
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.MovieFilesById[9] =
        [
            new LibraryVideoItem
            {
                Id = "gone-with-the-wind",
                FileName = "00003.m2ts",
                RelativePath = "Gone.with.the.Wind.1939.1080p.75th.Anniversary.Edition.Blu-ray.AVC.DTS-HD.MA5.1-DiY@HDHome/Disc 1 - Gone with the Wind - The Movie/BDMV/STREAM/00003.m2ts",
                AbsolutePath = "https://demo.example/library/Gone.with.the.Wind.1939/Disc%201%20-%20Gone%20with%20the%20Wind%20-%20The%20Movie/BDMV/STREAM/00003.m2ts",
                PlaybackPath = "https://demo.example/library/Gone.with.the.Wind.1939/Disc%201%20-%20Gone%20with%20the%20Wind%20-%20The%20Movie/BDMV/STREAM/00003.m2ts"
            }
        ];
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var poster = new LibraryPosterItem
        {
            Id = "movie-9",
            Title = "- Gone with the Wind - The Movie",
            Subtitle = "1939",
            MediaKind = "电影",
            MovieId = 9
        };

        await viewModel.OpenPosterScrapeCommand.ExecuteAsync(poster);

        Assert.Equal("Gone with the Wind", viewModel.PosterScrapeQuery);
        Assert.Equal("1939", viewModel.PosterScrapeYear);
    }

    [Theory]
    [InlineData(
        "The.Glory.S01.2160p.NF.WEB-DL.DDP5.1.Atmos.DV.HDR.HEVC-HHWEB/The.Glory.S01E01.Episode.1.2160p.NF.WEB-DL.DDP5.1.Atmos.DV.HDR.HEVC-HHWEB.mkv",
        "The Glory",
        "")]
    [InlineData(
        "CCTV4K.Aerial.China.S01.Complete.2020.UHDTV.HEVC.HLG.DD5.1-CMCTV/CCTV4K.Aerial.China.S01E01.2020.UHDTV.HEVC.HLG.DD5.1-CMCTV.ts",
        "Aerial China",
        "2020")]
    [InlineData(
        "剧场版 银河铁道999 1979 2160P ULTRA-HD Blu-ray HEVC Atmos 7.1-SweetDreamDay/BDMV/STREAM/00002.m2ts",
        "银河铁道999",
        "1979")]
    [InlineData(
        "Casablanca 1942 70th Anniversary Blu-ray 1080P AVC DTS-HD MA1.0 -Chinagear@HDSky/卡萨布兰卡（70周年纪念版）.iso",
        "卡萨布兰卡",
        "1942")]
    [InlineData(
        "Casablanca 1942 70th Anniversary Blu-ray 1080P AVC DTS-HD MA1.0 -Chinagear@HDSky/卡萨布兰卡（花絮）.iso",
        "卡萨布兰卡",
        "1942")]
    public async Task OpenPosterScrapeCommand_CleansNoisySourceTitleForDefaultQuery(
        string relativePath,
        string expectedQuery,
        string expectedYear)
    {
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.MovieFilesById[9] =
        [
            new LibraryVideoItem
            {
                Id = "noisy-source",
                FileName = Path.GetFileName(relativePath),
                RelativePath = relativePath,
                AbsolutePath = relativePath,
                PlaybackPath = relativePath
            }
        ];
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        var poster = new LibraryPosterItem
        {
            Id = "movie-9",
            Title = "Current Title",
            MediaKind = "鐢靛奖",
            MovieId = 9
        };

        await viewModel.OpenPosterScrapeCommand.ExecuteAsync(poster);

        Assert.Equal(expectedQuery, viewModel.PosterScrapeQuery);
        Assert.Equal(expectedYear, viewModel.PosterScrapeYear);
    }

    [Fact]
    public void OpenEpisodeEditCommand_ShowsDecodedSourceRelativePath()
    {
        var video = new LibraryVideoItem
        {
            Id = "remote-episode",
            FileName = "%E7%AC%AC%201%20%E9%9B%86.mkv",
            RelativePath = "shows/%E7%A4%BA%E4%BE%8B%E5%89%A7/S01/%E7%AC%AC%201%20%E9%9B%86.mkv",
            AbsolutePath = @"\\192.168.0.150\media\shows\示例剧\S01\第 1 集.mkv",
            PlaybackPath = "https://demo.example/library/shows/Demo/%E7%AC%AC%201%20%E9%9B%86.mkv",
            IsTvEpisode = true,
            SeasonNumber = 1,
            EpisodeNumber = 1
        };
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()));
        viewModel.IsDetailSeries = true;

        viewModel.OpenEpisodeEditCommand.Execute(video);

        Assert.Equal("shows/示例剧/S01/第 1 集.mkv", viewModel.EpisodeEditSourceFileText);
    }

    [Fact]
    public void EpisodeDisplaySubtitle_IgnoresParsedSubtitleUnlessCustomized()
    {
        var parsedOnly = new LibraryVideoItem
        {
            IsTvEpisode = true,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            EpisodeSubtitle = "Parsed Topic"
        };
        var customized = new LibraryVideoItem
        {
            IsTvEpisode = true,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            EpisodeSubtitle = "Parsed Topic",
            CustomEpisodeSubtitle = "Custom Topic"
        };

        Assert.Equal(string.Empty, parsedOnly.EpisodeDisplaySubtitle);
        Assert.False(parsedOnly.HasEpisodeDisplaySubtitle);
        Assert.Equal("Custom Topic", customized.EpisodeDisplaySubtitle);
        Assert.True(customized.HasEpisodeDisplaySubtitle);
    }

    [Fact]
    public async Task PlayPreviousEpisodeCommand_SwitchesToPreviousEpisodeAndPersistsCurrentProgress()
    {
        var episodeOnePath = CreateMediaFile();
        var episodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "E01"),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                playProgress: 45,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "E02")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var playbackLauncher = new FakePlaybackLauncher();
        var player = new PlayerViewModel(new FakeMediaPlayer());
        var viewModel = CreateViewModel(
            videoRepository,
            playbackLauncher,
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            player,
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);
        var secondEpisode = Assert.Single(viewModel.DetailFiles, file => file.Id == "ep-2");
        await viewModel.OpenStandalonePlayerCommand.ExecuteAsync(secondEpisode);

        player.CurrentPositionSeconds = 180;
        player.DurationSeconds = 300;
        player.IsPlaying = true;

        Assert.True(player.HasPreviousEpisodeAction);
        Assert.True(player.PlayPreviousEpisodeCommand.CanExecute(null));

        await player.PlayPreviousEpisodeCommand.ExecuteAsync(null);

        Assert.Equal(episodeOnePath, playbackLauncher.LastOpenedFilePath);
        Assert.Equal("ep-1", viewModel.DetailPrimaryFile?.Id);
        Assert.Equal("ep-2", videoRepository.LastUpdatedVideoId);
        Assert.Equal(180, videoRepository.LastUpdatedProgressSeconds);
        Assert.Equal(300, videoRepository.LastUpdatedDurationSeconds);
        Assert.Equal("ep-1", Assert.Single(player.PlaybackNavigationItems, item => item.IsCurrent).Id);
        Assert.True(playbackLauncher.LastReplaceCurrentSession);
    }

    [Fact]
    public async Task PlaybackCompletion_AutoPlaysNextEpisode()
    {
        var episodeOnePath = CreateMediaFile();
        var episodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                playProgress: 120,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "E01"),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "E02")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var player = new PlayerViewModel(new FakeMediaPlayer());
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            player,
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);
        await viewModel.PlayVideoCommand.ExecuteAsync(viewModel.DetailPrimaryFile);

        player.DurationSeconds = 300;
        player.CurrentPositionSeconds = 299;
        player.IsPlaying = true;
        player.IsPlaybackCompleted = true;

        await WaitUntilAsync(() => string.Equals(viewModel.DetailPrimaryFile?.Id, "ep-2", StringComparison.Ordinal));

        Assert.True(viewModel.IsPlayerOverlayOpen);
        Assert.Equal(episodeTwoPath, viewModel.PendingPlaybackFilePath);
        Assert.Equal("ep-2", viewModel.DetailPrimaryFile?.Id);

        var updatedEpisodeOne = videoRepository.FindVideo("ep-1");
        Assert.NotNull(updatedEpisodeOne);
        Assert.Equal(300, updatedEpisodeOne!.PlayProgress);
    }

    [Fact]
    public async Task SelectPlaybackNavigationItemCommand_ReopensStandalonePlayerOnSelectedEpisode()
    {
        var episodeOnePath = CreateMediaFile();
        var episodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                playProgress: 120,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "E01"),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "E02")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var playbackLauncher = new FakePlaybackLauncher();
        var player = new PlayerViewModel(new FakeMediaPlayer());
        var viewModel = CreateViewModel(
            videoRepository,
            playbackLauncher,
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            player,
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);
        await viewModel.OpenStandalonePrimaryCommand.ExecuteAsync(null);

        player.CurrentPositionSeconds = 150;
        player.DurationSeconds = 300;

        Assert.True(player.HasPlaybackNavigation);
        Assert.Equal(2, player.PlaybackNavigationItems.Count);

        var targetItem = Assert.Single(player.PlaybackNavigationItems, item => item.Id == "ep-2");
        Assert.True(player.SelectPlaybackNavigationItemCommand.CanExecute(targetItem));

        await player.SelectPlaybackNavigationItemCommand.ExecuteAsync(targetItem);

        Assert.Equal(episodeTwoPath, playbackLauncher.LastOpenedFilePath);
        Assert.Equal("ep-2", viewModel.DetailPrimaryFile?.Id);
        Assert.Equal("ep-1", videoRepository.LastUpdatedVideoId);
        Assert.Equal(150, videoRepository.LastUpdatedProgressSeconds);
        Assert.Equal(300, videoRepository.LastUpdatedDurationSeconds);
        Assert.Equal("ep-2", Assert.Single(player.PlaybackNavigationItems, item => item.IsCurrent).Id);
        Assert.True(playbackLauncher.LastReplaceCurrentSession);
    }

    [Fact]
    public async Task OpenStandalonePlayerCommand_WhenStandaloneSessionActive_ReplacesCurrentSessionAndPersistsProgress()
    {
        var episodeOnePath = CreateMediaFile();
        var episodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                episodeOnePath,
                id: "ep-1",
                playProgress: 30,
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "E01"),
            CreateVideoItem(
                episodeTwoPath,
                id: "ep-2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "E02")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var playbackLauncher = new FakePlaybackLauncher();
        var player = new PlayerViewModel(new FakeMediaPlayer());
        var viewModel = CreateViewModel(
            videoRepository,
            playbackLauncher,
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            player,
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);
        await viewModel.OpenStandalonePrimaryCommand.ExecuteAsync(null);

        player.CurrentPositionSeconds = 210;
        player.DurationSeconds = 300;
        player.IsPlaying = true;

        var secondEpisode = Assert.Single(viewModel.DetailFiles, file => file.Id == "ep-2");
        await viewModel.OpenStandalonePlayerCommand.ExecuteAsync(secondEpisode);

        Assert.Equal(episodeTwoPath, playbackLauncher.LastOpenedFilePath);
        Assert.True(playbackLauncher.LastReplaceCurrentSession);
        Assert.Equal("ep-1", videoRepository.LastUpdatedVideoId);
        Assert.Equal(210, videoRepository.LastUpdatedProgressSeconds);
        Assert.Equal(300, videoRepository.LastUpdatedDurationSeconds);
        Assert.Equal("ep-2", viewModel.DetailPrimaryFile?.Id);
    }

    [Fact]
    public async Task SelectedPlaybackNavigationSeason_UpdatesVisiblePlaybackNavigationItems()
    {
        var seasonOneEpisodeOnePath = CreateMediaFile();
        var seasonOneEpisodeTwoPath = CreateMediaFile();
        var seasonTwoEpisodeOnePath = CreateMediaFile();
        var seasonTwoEpisodeTwoPath = CreateMediaFile();
        var videoRepository = new FakeVideoFileRepository();
        videoRepository.TvShowFilesById[7] =
        [
            CreateVideoItem(
                seasonOneEpisodeOnePath,
                id: "s1e1",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 1,
                episodeLabel: "S1E1"),
            CreateVideoItem(
                seasonOneEpisodeTwoPath,
                id: "s1e2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 1,
                episodeNumber: 2,
                episodeLabel: "S1E2"),
            CreateVideoItem(
                seasonTwoEpisodeOnePath,
                id: "s2e1",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 2,
                episodeNumber: 1,
                episodeLabel: "S2E1"),
            CreateVideoItem(
                seasonTwoEpisodeTwoPath,
                id: "s2e2",
                duration: 300,
                isTvEpisode: true,
                seasonNumber: 2,
                episodeNumber: 2,
                episodeLabel: "S2E2")
        ];

        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });

        var player = new PlayerViewModel(new FakeMediaPlayer());
        var viewModel = CreateViewModel(
            videoRepository,
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            player,
            tvShowRepository: tvShowRepository);

        await viewModel.LoadAsync();
        var posterItem = Assert.Single(viewModel.LibraryItems);

        await viewModel.OpenDetailCommand.ExecuteAsync(posterItem);

        Assert.True(player.HasPlaybackNavigation);
        Assert.True(player.HasPlaybackNavigationSeasons);
        Assert.Equal([1, 2], player.PlaybackNavigationSeasons);
        Assert.Equal(["s1e1", "s1e2"], player.PlaybackNavigationItems.Select(item => item.Id).ToArray());

        player.SelectedPlaybackNavigationSeason = 2;
        await Task.Delay(20);

        Assert.Equal(["s2e1", "s2e2"], player.PlaybackNavigationItems.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task AddWebDavSourceAsync_SavesWebDavSourceWithBasicAuth()
    {
        var mediaSourceRepository = new FakeMediaSourceRepository();
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository);

        viewModel.PendingWebDavUrl = " https://demo.example/library/ ";
        viewModel.PendingWebDavUsername = "demo";
        viewModel.PendingWebDavPassword = "secret";

        await viewModel.AddWebDavSourceAsync();

        var source = Assert.Single(mediaSourceRepository.Sources);
        Assert.Equal("webdav", source.ProtocolType);
        Assert.Equal("https://demo.example/library", source.BaseUrl);
        Assert.Equal("library", source.Name);

        var auth = MediaSourceAuthConfigSerializer.DeserializeWebDav(source.AuthConfig);
        Assert.NotNull(auth);
        Assert.Equal("demo", auth!.Username);
        Assert.Equal("secret", auth.Password);
    }

    [Fact]
    public async Task MountNetworkFolderCommand_ClosesLoginAndStartsScanAndArtworkRefresh()
    {
        var mediaSourceRepository = new FakeMediaSourceRepository();
        var scanner = new FakeLibraryScanner();
        var tvShowRepository = new FakeTvShowRepository();
        var thumbnailEnricher = new FakeLibraryThumbnailEnricher();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show"
        });
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository,
            scanner,
            tvShowRepository: tvShowRepository,
            thumbnailEnricher: thumbnailEnricher);

        var folder = new NetworkShareFolderItem
        {
            Name = "Shows",
            ProtocolType = "webdav",
            BaseUrl = "https://demo.example/shows",
            AuthConfig = MediaSourceAuthConfigSerializer.SerializeWebDav(new WebDavAuthConfig("demo", "secret"))
        };
        viewModel.IsNetworkLoginPanelOpen = true;
        viewModel.PendingNetworkProtocolType = "webdav";
        viewModel.PendingNetworkBaseUrl = "https://demo.example";
        viewModel.PendingNetworkUsername = "demo";
        viewModel.PendingNetworkPassword = "secret";
        viewModel.NetworkShareFolders.Add(folder);

        await viewModel.MountNetworkFolderCommand.ExecuteAsync(folder);

        var source = Assert.Single(mediaSourceRepository.Sources);
        Assert.Equal("Shows", source.Name);
        Assert.Equal("webdav", source.ProtocolType);
        Assert.False(viewModel.IsNetworkLoginPanelOpen);
        Assert.Empty(viewModel.PendingNetworkBaseUrl);
        Assert.Empty(viewModel.PendingNetworkUsername);
        Assert.Empty(viewModel.PendingNetworkPassword);
        Assert.Empty(viewModel.NetworkShareFolders);
        Assert.Equal(1, scanner.CallCount);
        Assert.Equal(1, thumbnailEnricher.TvShowCallCount);
    }

    [Fact]
    public async Task EditSourceCommand_ForLocalSource_ReplacesFolderPathAndName()
    {
        var originalPath = CreateFolder();
        var replacementPath = CreateFolder();
        var mediaSourceRepository = new FakeMediaSourceRepository();
        var folderPicker = new FakeFolderPickerService
        {
            NextPath = replacementPath
        };

        var source = new MediaSource
        {
            Id = 1,
            Name = "Old Folder",
            ProtocolType = "local",
            BaseUrl = originalPath
        };
        mediaSourceRepository.Sources.Add(source);

        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository,
            folderPickerService: folderPicker);

        await viewModel.EditSourceCommand.ExecuteAsync(source);

        var storedSource = Assert.Single(mediaSourceRepository.Sources);
        Assert.Equal(replacementPath, storedSource.BaseUrl);
        Assert.Equal(Path.GetFileName(replacementPath), storedSource.Name);
        Assert.Equal(1, folderPicker.CallCount);
        Assert.Contains("已更新本地媒体源", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditSourceCommand_ForLocalSource_WhenPickerCancelled_KeepsSourceUnchanged()
    {
        var originalPath = CreateFolder();
        var mediaSourceRepository = new FakeMediaSourceRepository();
        var folderPicker = new FakeFolderPickerService();

        var source = new MediaSource
        {
            Id = 1,
            Name = "Local Folder",
            ProtocolType = "local",
            BaseUrl = originalPath
        };
        mediaSourceRepository.Sources.Add(source);

        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository,
            folderPickerService: folderPicker);

        await viewModel.EditSourceCommand.ExecuteAsync(source);

        var storedSource = Assert.Single(mediaSourceRepository.Sources);
        Assert.Equal(originalPath, storedSource.BaseUrl);
        Assert.Equal("Local Folder", storedSource.Name);
        Assert.Equal(1, folderPicker.CallCount);
        Assert.Contains("已取消更换目录", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_StoresLatestScanSummaryAndDiagnostics()
    {
        var scanner = new FakeLibraryScanner
        {
            Summary = new LibraryScanSummary(
                2,
                1,
                3,
                1,
                1,
                ["已跳过本地媒体源“失效目录”：目录不存在。"])
        };
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            scanner: scanner);

        await viewModel.ScanAsync();

        Assert.True(viewModel.HasLastScanSummary);
        Assert.True(viewModel.HasLastScanDiagnostics);
        Assert.Contains("最近扫描了 2 个媒体源", viewModel.LastScanOverviewText, StringComparison.Ordinal);
        Assert.Single(viewModel.LastScanDiagnostics);
        Assert.Contains("目录不存在", viewModel.LastScanDiagnostics[0], StringComparison.Ordinal);
        Assert.Contains("1 条扫描诊断", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_KeepsPosterEditingAndPlaybackCommandsAvailable()
    {
        var scanner = new FakeLibraryScanner
        {
            ScanStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueScan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var video = CreateVideoItem(
            CreateMediaFile(),
            isTvEpisode: true,
            seasonNumber: 1,
            episodeNumber: 1);
        var poster = new LibraryPosterItem
        {
            Id = "tv-7",
            Title = "Demo Show",
            MediaKind = "剧集",
            TvShowId = 7
        };
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            scanner: scanner);
        viewModel.DetailPrimaryFile = video;
        viewModel.IsDetailSeries = true;

        var scanTask = viewModel.ScanAsync();
        await scanner.ScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.ScanCommand.CanExecute(null));
        Assert.True(viewModel.OpenPosterScrapeCommand.CanExecute(poster));
        Assert.True(viewModel.OpenPosterEditCommand.CanExecute(poster));
        Assert.True(viewModel.OpenEpisodeEditCommand.CanExecute(video));
        Assert.True(viewModel.PlayPrimaryCommand.CanExecute(null));

        scanner.ContinueScan.SetResult(true);
        await scanTask;
    }

    [Fact]
    public async Task RemoveSourceCommand_CancelsRunningScanAsync()
    {
        var sourcePath = CreateFolder();
        var source = new MediaSource
        {
            Id = 1,
            Name = "Mounted Folder",
            ProtocolType = "local",
            BaseUrl = sourcePath
        };
        var mediaSourceRepository = new FakeMediaSourceRepository();
        mediaSourceRepository.Sources.Add(source);
        var scanner = new FakeLibraryScanner
        {
            ScanStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueScan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            CancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository,
            scanner: scanner);

        var scanTask = viewModel.ScanAsync();
        await scanner.ScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(viewModel.RemoveSourceCommand.CanExecute(source));
        await viewModel.RemoveSourceCommand.ExecuteAsync(source);

        await scanner.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await scanTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(mediaSourceRepository.Sources);
    }

    [Fact]
    public async Task RemoveSourceCommand_CancelsRunningMetadataScrapeAsync()
    {
        var source = new MediaSource
        {
            Id = 1,
            Name = "Mounted Folder",
            ProtocolType = "local",
            BaseUrl = CreateFolder()
        };
        var mediaSourceRepository = new FakeMediaSourceRepository();
        mediaSourceRepository.Sources.Add(source);
        var movieRepository = new FakeMovieRepository();
        movieRepository.Movies.Add(new Movie
        {
            Id = 9,
            Title = "Demo Movie"
        });
        var metadataEnricher = new FakeLibraryMetadataEnricher
        {
            MovieMetadataStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueMovieMetadata = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            MovieMetadataCancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository,
            scanner: new FakeLibraryScanner(),
            movieRepository: movieRepository,
            metadataEnricher: metadataEnricher);

        var scanTask = viewModel.ScanAsync();
        await metadataEnricher.MovieMetadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await viewModel.RemoveSourceCommand.ExecuteAsync(source);

        await metadataEnricher.MovieMetadataCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await scanTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(mediaSourceRepository.Sources);
    }

    [Fact]
    public async Task RemoveSourceCommand_CancelsRunningEpisodeThumbnailScrapeAsync()
    {
        var source = new MediaSource
        {
            Id = 1,
            Name = "Mounted Folder",
            ProtocolType = "local",
            BaseUrl = CreateFolder()
        };
        var mediaSourceRepository = new FakeMediaSourceRepository();
        mediaSourceRepository.Sources.Add(source);
        var tvShowRepository = new FakeTvShowRepository();
        tvShowRepository.Shows.Add(new TvShow
        {
            Id = 7,
            Title = "Demo Show",
            FirstAirDate = "2024-01-01",
            Overview = "Overview",
            PosterPath = CreateMediaFile(),
            VoteAverage = 8.2,
            ProductionCountryCodes = "US"
        });
        var thumbnailEnricher = new FakeLibraryThumbnailEnricher
        {
            TvShowThumbnailStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueTvShowThumbnail = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            TvShowThumbnailCancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester()),
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository,
            scanner: new FakeLibraryScanner(),
            tvShowRepository: tvShowRepository,
            thumbnailEnricher: thumbnailEnricher);

        var scanTask = viewModel.ScanAsync();
        await thumbnailEnricher.TvShowThumbnailStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await viewModel.RemoveSourceCommand.ExecuteAsync(source);

        await thumbnailEnricher.TvShowThumbnailCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await scanTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(mediaSourceRepository.Sources);
    }

    [Fact]
    public async Task RemoveSourceCommand_DropsQueuedArtworkRefreshAsync()
    {
        var source = new MediaSource
        {
            Id = 1,
            Name = "Mounted Folder",
            ProtocolType = "local",
            BaseUrl = CreateFolder()
        };
        var mediaSourceRepository = new FakeMediaSourceRepository();
        mediaSourceRepository.Sources.Add(source);
        var movieRepository = new FakeMovieRepository();
        movieRepository.Movies.Add(new Movie
        {
            Id = 9,
            Title = "Demo Movie"
        });
        var metadataEnricher = new FakeLibraryMetadataEnricher
        {
            MovieMetadataStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueMovieMetadata = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            MovieMetadataCancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var settings = new SettingsViewModel(new FakeSettingsService(), new FakeTmdbConnectionTester());
        var viewModel = CreateViewModel(
            new FakeVideoFileRepository(),
            new FakePlaybackLauncher(),
            settings,
            new PlayerViewModel(new FakeMediaPlayer()),
            mediaSourceRepository,
            movieRepository: movieRepository,
            metadataEnricher: metadataEnricher);

        await viewModel.LoadAsync();
        await settings.SaveCommand.ExecuteAsync(null);
        await metadataEnricher.MovieMetadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await settings.SaveCommand.ExecuteAsync(null);
        await viewModel.RemoveSourceCommand.ExecuteAsync(source);

        await metadataEnricher.MovieMetadataCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.Equal(1, metadataEnricher.MovieMetadataCallCount);
    }

    private static PosterWallViewModel CreateViewModel(
        FakeVideoFileRepository videoRepository,
        FakePlaybackLauncher playbackLauncher,
        SettingsViewModel settings,
        PlayerViewModel player,
        FakeMediaSourceRepository? mediaSourceRepository = null,
        FakeLibraryScanner? scanner = null,
        FakeFolderPickerService? folderPickerService = null,
        FakeMovieRepository? movieRepository = null,
        FakeTvShowRepository? tvShowRepository = null,
        FakeLibraryMetadataEditor? metadataEditor = null,
        FakeLibraryMetadataEnricher? metadataEnricher = null,
        FakeLibraryThumbnailEnricher? thumbnailEnricher = null)
    {
        return new PosterWallViewModel(
            movieRepository ?? new FakeMovieRepository(),
            tvShowRepository ?? new FakeTvShowRepository(),
            mediaSourceRepository ?? new FakeMediaSourceRepository(),
            videoRepository,
            scanner ?? new FakeLibraryScanner(),
            metadataEditor ?? new FakeLibraryMetadataEditor(),
            metadataEnricher ?? new FakeLibraryMetadataEnricher(),
            thumbnailEnricher ?? new FakeLibraryThumbnailEnricher(),
            folderPickerService ?? new FakeFolderPickerService(),
            new FakePosterImagePickerService(),
            new FakeWebDavConnectionTester(),
            new FakeNetworkShareDiscoveryService(),
            playbackLauncher,
            settings,
            player);
    }

    private static LibraryVideoItem CreateVideoItem(
        string filePath,
        string id = "video-1",
        double playProgress = 0,
        double duration = 300,
        bool isTvEpisode = false,
        int seasonNumber = 1,
        int episodeNumber = 1,
        string? episodeLabel = null)
    {
        return new LibraryVideoItem
        {
            Id = id,
            FileName = Path.GetFileName(filePath),
            AbsolutePath = filePath,
            PlaybackPath = filePath,
            PlayProgress = playProgress,
            Duration = duration,
            IsTvEpisode = isTvEpisode,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeLabel = episodeLabel ?? string.Empty
        };
    }

    private static string CreateMediaFile()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"omniplay-{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(filePath, [1, 2, 3, 4]);
        return filePath;
    }

    private static string CreateFolder()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"omniplay-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 1000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMilliseconds)
            {
                throw new TimeoutException("Condition was not met within the allotted time.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class FakePlaybackLauncher : IPlaybackLauncher
    {
        private Func<PlaybackCloseResult, Task>? onPlaybackClosed;

        public string? LastOpenedFilePath { get; private set; }

        public double? LastStartPositionSeconds { get; private set; }

        public bool LastReplaceCurrentSession { get; private set; }

        public Task<bool> OpenAsync(
            PlaybackOpenRequest request,
            Func<PlaybackCloseResult, Task>? onPlaybackClosed = null,
            double? startPositionSeconds = null,
            bool replaceCurrentSession = false,
            CancellationToken cancellationToken = default)
        {
            LastOpenedFilePath = request.PlaybackPath;
            LastStartPositionSeconds = startPositionSeconds;
            LastReplaceCurrentSession = replaceCurrentSession;
            this.onPlaybackClosed = onPlaybackClosed;
            return Task.FromResult(true);
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task CompleteAsync(PlaybackCloseResult result)
        {
            return onPlaybackClosed is null ? Task.CompletedTask : onPlaybackClosed(result);
        }
    }

    private sealed class FakeVideoFileRepository : IVideoFileRepository
    {
        public Dictionary<long, List<LibraryVideoItem>> MovieFilesById { get; } = [];

        public Dictionary<long, List<LibraryVideoItem>> TvShowFilesById { get; } = [];

        public string? LastUpdatedVideoId { get; private set; }

        public double LastUpdatedProgressSeconds { get; private set; }

        public double LastUpdatedDurationSeconds { get; private set; }

        public int GetByTvShowCallCount { get; private set; }

        public int GetContinueWatchingCallCount { get; private set; }

        public Task<IReadOnlyList<LibraryVideoItem>> GetByMovieAsync(long movieId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LibraryVideoItem>>(CloneFiles(MovieFilesById, movieId));
        }

        public Task<IReadOnlyList<LibraryVideoItem>> GetByTvShowAsync(long tvShowId, CancellationToken cancellationToken = default)
        {
            GetByTvShowCallCount++;
            return Task.FromResult<IReadOnlyList<LibraryVideoItem>>(CloneFiles(TvShowFilesById, tvShowId));
        }

        public Task<IReadOnlyList<LibraryPosterItem>> GetContinueWatchingAsync(CancellationToken cancellationToken = default)
        {
            GetContinueWatchingCallCount++;
            return Task.FromResult<IReadOnlyList<LibraryPosterItem>>([]);
        }

        public Task<IReadOnlyDictionary<string, PlaybackWatchState>> GetLibraryPlaybackStatesAsync(CancellationToken cancellationToken = default)
        {
            var states = new Dictionary<string, PlaybackWatchState>(StringComparer.OrdinalIgnoreCase);

            foreach (var (movieId, files) in MovieFilesById)
            {
                states[$"movie-{movieId}"] = ResolveMovieWatchState(files);
            }

            foreach (var (tvShowId, files) in TvShowFilesById)
            {
                states[$"tv-{tvShowId}"] = ResolveTvShowWatchState(files);
            }

            return Task.FromResult<IReadOnlyDictionary<string, PlaybackWatchState>>(states);
        }

        public Task UpdatePlayProgressAsync(string videoFileId, double playProgress, CancellationToken cancellationToken = default)
        {
            LastUpdatedVideoId = videoFileId;
            LastUpdatedProgressSeconds = playProgress;
            UpdateVideo(
                videoFileId,
                file => CloneVideo(file, playProgress: playProgress));
            return Task.CompletedTask;
        }

        public Task UpdatePlaybackStateAsync(
            string videoFileId,
            double playProgress,
            double? durationSeconds,
            CancellationToken cancellationToken = default)
        {
            LastUpdatedVideoId = videoFileId;
            LastUpdatedProgressSeconds = playProgress;
            LastUpdatedDurationSeconds = durationSeconds ?? 0;
            UpdateVideo(
                videoFileId,
                file => CloneVideo(
                    file,
                    playProgress: playProgress,
                    duration: durationSeconds ?? file.Duration));
            return Task.CompletedTask;
        }

        public Task UpdateEpisodeMetadataAsync(
            string videoFileId,
            LibraryEpisodeEditRequest request,
            CancellationToken cancellationToken = default)
        {
            UpdateVideo(
                videoFileId,
                file => CloneVideo(
                    file,
                    seasonNumber: request.SeasonNumber ?? file.SeasonNumber,
                    episodeNumber: request.EpisodeNumber ?? file.EpisodeNumber,
                    episodeYear: string.IsNullOrWhiteSpace(request.Year) ? null : request.Year.Trim(),
                    episodeSubtitle: string.IsNullOrWhiteSpace(request.Subtitle) ? null : request.Subtitle.Trim(),
                    customThumbnailPath: string.IsNullOrWhiteSpace(request.ThumbnailPath) ? null : request.ThumbnailPath.Trim()));
            return Task.CompletedTask;
        }

        public LibraryVideoItem? FindVideo(string videoId)
        {
            return MovieFilesById.Values
                .Concat(TvShowFilesById.Values)
                .SelectMany(static files => files)
                .FirstOrDefault(file => string.Equals(file.Id, videoId, StringComparison.Ordinal));
        }

        private static IReadOnlyList<LibraryVideoItem> CloneFiles(
            IReadOnlyDictionary<long, List<LibraryVideoItem>> source,
            long key)
        {
            if (!source.TryGetValue(key, out var files))
            {
                return [];
            }

            return files.Select(static file => CloneVideo(file)).ToList();
        }

        private void UpdateVideo(string videoId, Func<LibraryVideoItem, LibraryVideoItem> updater)
        {
            foreach (var entry in MovieFilesById)
            {
                if (UpdateVideo(entry.Value, videoId, updater))
                {
                    return;
                }
            }

            foreach (var entry in TvShowFilesById)
            {
                if (UpdateVideo(entry.Value, videoId, updater))
                {
                    return;
                }
            }
        }

        private static bool UpdateVideo(
            IList<LibraryVideoItem> files,
            string videoId,
            Func<LibraryVideoItem, LibraryVideoItem> updater)
        {
            for (var index = 0; index < files.Count; index++)
            {
                if (!string.Equals(files[index].Id, videoId, StringComparison.Ordinal))
                {
                    continue;
                }

                files[index] = updater(files[index]);
                return true;
            }

            return false;
        }

        private static LibraryVideoItem CloneVideo(
            LibraryVideoItem source,
            double? playProgress = null,
            double? duration = null,
            int? seasonNumber = null,
            int? episodeNumber = null,
            string? episodeYear = null,
            string? episodeSubtitle = null,
            string? customThumbnailPath = null)
        {
            return new LibraryVideoItem
            {
                Id = source.Id,
                FileName = source.FileName,
                RelativePath = source.RelativePath,
                AbsolutePath = source.AbsolutePath,
                PlaybackPath = source.PlaybackPath,
                ThumbnailPath = source.ThumbnailPath,
                CustomThumbnailPath = customThumbnailPath ?? source.CustomThumbnailPath,
                FallbackImagePath = source.FallbackImagePath,
                PlayProgress = playProgress ?? source.PlayProgress,
                Duration = duration ?? source.Duration,
                SeasonNumber = seasonNumber ?? source.SeasonNumber,
                EpisodeNumber = episodeNumber ?? source.EpisodeNumber,
                IsTvEpisode = source.IsTvEpisode,
                EpisodeYear = episodeYear ?? source.EpisodeYear,
                CustomEpisodeSubtitle = episodeSubtitle ?? source.CustomEpisodeSubtitle,
                EpisodeSubtitle = episodeSubtitle ?? source.EpisodeSubtitle,
                EpisodeLabel = source.IsTvEpisode
                    ? $"S{(seasonNumber ?? source.SeasonNumber):00}E{(episodeNumber ?? source.EpisodeNumber):00}"
                    : source.EpisodeLabel
            };
        }

        private static PlaybackWatchState ResolveMovieWatchState(IReadOnlyList<LibraryVideoItem> files)
        {
            if (files.Count == 0 || files.All(static file => !file.HasProgress))
            {
                return PlaybackWatchState.Unwatched;
            }

            return files.Any(static file => file.IsWatched)
                ? PlaybackWatchState.Watched
                : PlaybackWatchState.InProgress;
        }

        private static PlaybackWatchState ResolveTvShowWatchState(IReadOnlyList<LibraryVideoItem> files)
        {
            if (files.Count == 0 || files.All(static file => !file.HasProgress))
            {
                return PlaybackWatchState.Unwatched;
            }

            return files.All(static file => file.IsWatched)
                ? PlaybackWatchState.Watched
                : PlaybackWatchState.InProgress;
        }
    }

    private sealed class FakeMovieRepository : IMovieRepository
    {
        public List<Movie> Movies { get; } = [];

        public Task<IReadOnlyList<Movie>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Movie>>(Movies);
        }
    }

    private sealed class FakeTvShowRepository : ITvShowRepository
    {
        public List<TvShow> Shows { get; } = [];

        public Task<IReadOnlyList<TvShow>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TvShow>>(Shows);
        }
    }

    private sealed class FakeMediaSourceRepository : IMediaSourceRepository
    {
        public List<MediaSource> Sources { get; } = [];

        public Task<long> AddAsync(MediaSource source, CancellationToken cancellationToken = default)
        {
            Sources.Add(new MediaSource
            {
                Id = Sources.Count + 1,
                Name = source.Name,
                ProtocolType = source.ProtocolType,
                BaseUrl = source.GetNormalizedBaseUrl(),
                AuthConfig = source.AuthConfig,
                IsEnabled = source.IsEnabled,
                DisabledAt = source.DisabledAt,
                RemovedAt = source.RemovedAt
            });
            return Task.FromResult(1L);
        }

        public Task<IReadOnlyList<MediaSource>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MediaSource>>(Sources);
        }

        public Task<bool> UpdateAsync(MediaSource source, CancellationToken cancellationToken = default)
        {
            if (source.Id is null)
            {
                return Task.FromResult(false);
            }

            var index = Sources.FindIndex(existing => existing.Id == source.Id.Value);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Sources[index] = new MediaSource
            {
                Id = source.Id.Value,
                Name = source.Name,
                ProtocolType = source.ProtocolType,
                BaseUrl = source.GetNormalizedBaseUrl(),
                AuthConfig = source.AuthConfig,
                IsEnabled = source.IsEnabled,
                DisabledAt = source.DisabledAt,
                RemovedAt = source.RemovedAt
            };
            return Task.FromResult(true);
        }

        public Task<bool> SetEnabledAsync(long id, bool isEnabled, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var source = Sources.FirstOrDefault(source => source.Id == id);
            if (source is null)
            {
                return Task.FromResult(false);
            }

            source.IsEnabled = isEnabled;
            source.DisabledAt = isEnabled ? null : now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            source.RemovedAt = null;
            return Task.FromResult(true);
        }

        public Task<bool> SoftRemoveAsync(long id, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var source = Sources.FirstOrDefault(source => source.Id == id);
            if (source is null)
            {
                return Task.FromResult(false);
            }

            source.IsEnabled = false;
            source.DisabledAt ??= now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            source.RemovedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            Sources.Remove(source);
            return Task.FromResult(true);
        }

        public Task<int> PurgeExpiredInactiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
        {
            var removed = Sources.RemoveAll(source => source.Id == id) > 0;
            return Task.FromResult(removed);
        }
    }

    private sealed class FakeLibraryScanner : ILibraryScanner
    {
        public LibraryScanSummary Summary { get; set; } = new(0, 0, 0);

        public int CallCount { get; private set; }

        public TaskCompletionSource<bool>? ScanStarted { get; set; }

        public TaskCompletionSource<bool>? ContinueScan { get; set; }

        public TaskCompletionSource<bool>? CancellationObserved { get; set; }

        public async Task<LibraryScanSummary> ScanAllAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            ScanStarted?.TrySetResult(true);
            if (ContinueScan is not null)
            {
                try
                {
                    await ContinueScan.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved?.TrySetResult(true);
                    throw;
                }
            }

            return Summary;
        }
    }

    private sealed class FakeLibraryMetadataEditor : ILibraryMetadataEditor
    {
        public LibraryMetadataRefreshResult ApplyTvShowResult { get; set; } = new();

        public int ApplyTvShowCallCount { get; private set; }

        public Task<IReadOnlyList<LibraryMetadataSearchCandidate>> SearchMovieMatchesAsync(long movieId, string? manualQuery = null, string? manualYear = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LibraryMetadataSearchCandidate>>([]);

        public Task<IReadOnlyList<LibraryMetadataSearchCandidate>> SearchTvShowMatchesAsync(long tvShowId, string? manualQuery = null, string? manualYear = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LibraryMetadataSearchCandidate>>([]);

        public Task<LibraryMetadataRefreshResult> RefreshMovieAsync(long movieId, string? manualQuery = null, string? manualYear = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LibraryMetadataRefreshResult());

        public Task<LibraryMetadataRefreshResult> RefreshTvShowAsync(long tvShowId, string? manualQuery = null, string? manualYear = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LibraryMetadataRefreshResult());

        public Task<LibraryMetadataRefreshResult> ApplyMovieMatchAsync(long movieId, LibraryMetadataSearchCandidate candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(new LibraryMetadataRefreshResult());

        public Task<LibraryMetadataRefreshResult> ApplyTvShowMatchAsync(long tvShowId, LibraryMetadataSearchCandidate candidate, CancellationToken cancellationToken = default)
        {
            ApplyTvShowCallCount++;
            return Task.FromResult(ApplyTvShowResult);
        }

        public Task<LibraryMetadataRefreshResult> UpdateMovieMetadataAsync(long movieId, LibraryMetadataEditRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LibraryMetadataRefreshResult(Updated: true));

        public Task<LibraryMetadataRefreshResult> UpdateTvShowMetadataAsync(long tvShowId, LibraryMetadataEditRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LibraryMetadataRefreshResult(Updated: true));

        public Task SetMovieLockedAsync(long movieId, bool isLocked, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetTvShowLockedAsync(long tvShowId, bool isLocked, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeLibraryMetadataEnricher : ILibraryMetadataEnricher
    {
        public TaskCompletionSource<bool>? MovieMetadataStarted { get; set; }

        public TaskCompletionSource<bool>? ContinueMovieMetadata { get; set; }

        public TaskCompletionSource<bool>? MovieMetadataCancellationObserved { get; set; }

        public int MovieMetadataCallCount { get; private set; }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingMetadataAsync(TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LibraryMetadataEnrichmentSummary());
        }

        public async Task<LibraryMetadataEnrichmentSummary> EnrichMissingMovieMetadataAsync(long movieId, TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            MovieMetadataCallCount++;
            MovieMetadataStarted?.TrySetResult(true);
            if (ContinueMovieMetadata is not null)
            {
                try
                {
                    await ContinueMovieMetadata.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    MovieMetadataCancellationObserved?.TrySetResult(true);
                    throw;
                }
            }

            return new LibraryMetadataEnrichmentSummary();
        }

        public Task<LibraryMetadataEnrichmentSummary> EnrichMissingTvShowMetadataAsync(long tvShowId, TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LibraryMetadataEnrichmentSummary());
        }
    }

    private sealed class FakeLibraryThumbnailEnricher : ILibraryThumbnailEnricher
    {
        public LibraryThumbnailEnrichmentSummary Summary { get; set; } = new();

        public int GlobalCallCount { get; private set; }

        public int TvShowCallCount { get; private set; }

        public long? LastTvShowId { get; private set; }

        public TmdbSettings? LastSettings { get; private set; }

        public TaskCompletionSource<bool>? TvShowThumbnailStarted { get; set; }

        public TaskCompletionSource<bool>? ContinueTvShowThumbnail { get; set; }

        public TaskCompletionSource<bool>? TvShowThumbnailCancellationObserved { get; set; }

        public Task<LibraryThumbnailEnrichmentSummary> EnrichMissingThumbnailsAsync(TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            GlobalCallCount++;
            LastSettings = settings;
            return Task.FromResult(Summary);
        }

        public async Task<LibraryThumbnailEnrichmentSummary> EnrichMissingThumbnailsForTvShowAsync(long tvShowId, TmdbSettings? settings = null, CancellationToken cancellationToken = default)
        {
            TvShowCallCount++;
            LastTvShowId = tvShowId;
            LastSettings = settings;
            TvShowThumbnailStarted?.TrySetResult(true);
            if (ContinueTvShowThumbnail is not null)
            {
                try
                {
                    await ContinueTvShowThumbnail.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TvShowThumbnailCancellationObserved?.TrySetResult(true);
                    throw;
                }
            }

            return Summary;
        }
    }

    private sealed class FakeFolderPickerService : IFolderPickerService
    {
        public string? NextPath { get; set; }

        public int CallCount { get; private set; }

        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(NextPath);
        }
    }

    private sealed class FakePosterImagePickerService : IPosterImagePickerService
    {
        public Task<string?> PickPosterImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public string SettingsDirectory => Path.GetTempPath();

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppSettings());
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTmdbConnectionTester : ITmdbConnectionTester
    {
        public Task<TmdbConnectionTestResult> TestConnectionAsync(TmdbSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TmdbConnectionTestResult(true, "ok"));
        }
    }

    private sealed class FakeWebDavConnectionTester : IWebDavConnectionTester
    {
        public Task<WebDavConnectionTestResult> TestConnectionAsync(MediaSource source, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WebDavConnectionTestResult(true, "ok"));
        }
    }

    private sealed class FakeNetworkShareDiscoveryService : INetworkShareDiscoveryService
    {
        public Task<IReadOnlyList<NetworkSourceDiscoveryItem>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NetworkSourceDiscoveryItem>>([]);
        }

        public Task<IReadOnlyList<NetworkShareFolderItem>> ListFoldersAsync(
            NetworkSourceDiscoveryItem source,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NetworkShareFolderItem>>([]);
        }
    }

    private sealed class FakeMediaPlayer : IMediaPlayer
    {
        public bool IsAvailable => true;

        public string BackendName => "fake";

        public void Initialize()
        {
        }

        public void AttachToHost(IntPtr hostHandle)
        {
        }

        public Task<MediaPlayerOpenResult> OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MediaPlayerOpenResult.Success("opened"));
        }

        public Task<PlayerPlaybackState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlayerPlaybackState.Empty);
        }

        public Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SeekAsync(double positionSeconds, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SelectAudioTrackAsync(long? trackId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SelectSubtitleTrackAsync(long? trackId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> LoadExternalSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task SetSubtitleDelayAsync(double delaySeconds, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SetSubtitleFontSizeAsync(int fontSize, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
