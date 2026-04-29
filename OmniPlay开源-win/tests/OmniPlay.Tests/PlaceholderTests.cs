using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;
using OmniPlay.Infrastructure.Library;

namespace OmniPlay.Tests;

public sealed class PlaceholderTests
{
    [Fact]
    public void NormalizeLocalMediaSource_RemovesTrailingSlash()
    {
        var source = new MediaSource
        {
            ProtocolType = "local",
            BaseUrl = @"D:\Movies\"
        };

        Assert.Equal(@"D:\Movies", source.GetNormalizedBaseUrl());
    }

    [Fact]
    public void ExtractSearchMetadata_PullsForeignTitleAndYear()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(@"D:\Movies\Inception.2010.1080p.BluRay.x264.mkv");

        Assert.Equal("Inception", metadata.ForeignTitle);
        Assert.Equal("2010", metadata.Year);
    }

    [Fact]
    public void ExtractSearchMetadata_UsesTitleBeforeYearInsteadOfReleaseTail()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "American Beauty 1999 Paramount Blu-ray 1080p AVC DTS-HD MA 5.1-blucook#262@CHDBits/American.Beauty.1999.Paramount.Blu-ray.1080p.AVC.DTS-HD.MA.5.1@blucook#262.iso");

        Assert.Equal("American Beauty", metadata.ForeignTitle);
        Assert.Equal("American Beauty", metadata.FullCleanTitle);
        Assert.Equal("1999", metadata.Year);
    }

    [Fact]
    public void ExtractSearchMetadata_RemovesDiscPrefixDashAndTheMovieSuffix()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "Gone.with.the.Wind.1939.1080p.75th.Anniversary.Edition.Blu-ray.AVC.DTS-HD.MA5.1-DiY@HDHome/Disc 1 - Gone with the Wind - The Movie/BDMV/STREAM/00003.m2ts");

        Assert.Equal("Gone with the Wind", metadata.ForeignTitle);
        Assert.Equal("Gone with the Wind", metadata.FullCleanTitle);
    }

    [Fact]
    public void ExtractSearchMetadata_RemovesEpisodeAndReleaseTailFromTvFile()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "The.Glory.S01.2160p.NF.WEB-DL.DDP5.1.Atmos.DV.HDR.HEVC-HHWEB/The.Glory.S01E01.Episode.1.2160p.NF.WEB-DL.DDP5.1.Atmos.DV.HDR.HEVC-HHWEB.mkv");

        Assert.Equal("The Glory", metadata.ForeignTitle);
        Assert.Equal("The Glory", metadata.FullCleanTitle);
    }

    [Fact]
    public void ExtractSearchMetadata_RemovesTvStationPrefix()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "CCTV4K.Aerial.China.S01.Complete.2020.UHDTV.HEVC.HLG.DD5.1-CMCTV/CCTV4K.Aerial.China.S01E01.2020.UHDTV.HEVC.HLG.DD5.1-CMCTV.ts");

        Assert.Equal("Aerial China", metadata.ForeignTitle);
        Assert.Equal("2020", metadata.Year);
    }

    [Fact]
    public void ExtractSearchMetadata_RecognizesYearBeforeChineseYearSuffix()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(
            "shows/万物生灵.All.Creatures.Great.and.Small.2020年/万物生灵.All.Creatures.Great.and.Small.S01E01.2020年.mkv");

        Assert.Equal("2020", metadata.Year);
        Assert.Equal("万物生灵", metadata.ChineseTitle);
    }

    [Fact]
    public void ExtractSearchMetadata_RemovesChineseEditionNoise()
    {
        var theatrical = MediaNameParser.ExtractSearchMetadata(
            "剧场版 银河铁道999 1979 2160P ULTRA-HD Blu-ray HEVC Atmos 7.1-SweetDreamDay/BDMV/STREAM/00002.m2ts");
        var anniversary = MediaNameParser.ExtractSearchMetadata(
            "Casablanca 1942 70th Anniversary Blu-ray 1080P AVC DTS-HD MA1.0 -Chinagear@HDSky/卡萨布兰卡（70周年纪念版）.iso");
        var extras = MediaNameParser.ExtractSearchMetadata(
            "Casablanca 1942 70th Anniversary Blu-ray 1080P AVC DTS-HD MA1.0 -Chinagear@HDSky/卡萨布兰卡（花絮）.iso");

        Assert.Equal("银河铁道999", theatrical.ChineseTitle);
        Assert.Equal("卡萨布兰卡", anniversary.ChineseTitle);
        Assert.Equal("卡萨布兰卡", extras.ChineseTitle);
    }

    [Fact]
    public void ExtractSearchMetadata_PreservesNumericTitleBeforeReleaseYear()
    {
        var metadata = MediaNameParser.ExtractSearchMetadata(@"D:\Movies\1917.2019.2160p.BluRay.mkv");

        Assert.Equal("1917", metadata.FullCleanTitle);
        Assert.Equal("2019", metadata.Year);
    }

    [Fact]
    public void CleanedTitleSource_UsesFolderBeforeBdmv()
    {
        var title = MediaNameParser.CleanedTitleSource(@"D:\Movies\Interstellar\BDMV\STREAM\00001.m2ts");

        Assert.Equal("Interstellar", title);
    }

    [Fact]
    public void ParseEpisodeInfo_RecognizesSeasonEpisodePattern()
    {
        var episode = MediaNameParser.ParseEpisodeInfo("Show.S02E03.mkv", 0);

        Assert.True(episode.IsTvShow);
        Assert.Equal(2, episode.Season);
        Assert.Equal(3, episode.Episode);
    }

    [Fact]
    public void ParseEpisodeInfo_RecognizesMultipartEpisodePattern()
    {
        var partOne = MediaNameParser.ParseEpisodeInfo("Show.S01E01.part1.mkv", 0);
        var partTwo = MediaNameParser.ParseEpisodeInfo("Show.S01E01.part2.mkv", 1);

        Assert.True(partOne.IsTvShow);
        Assert.True(partTwo.IsTvShow);
        Assert.Equal(1, partOne.Season);
        Assert.Equal(1, partOne.Episode);
        Assert.Equal(1, partOne.PartNumber);
        Assert.Equal(1, partTwo.Season);
        Assert.Equal(1, partTwo.Episode);
        Assert.Equal(2, partTwo.PartNumber);
    }

    [Fact]
    public void ParseEpisodeInfo_IgnoresSubtitleAfterSeasonEpisodePattern()
    {
        var episode = MediaNameParser.ParseEpisodeInfo("Show.S01E01.Opening.Guest.Topic.mkv", 0);

        Assert.True(episode.IsTvShow);
        Assert.Equal(1, episode.Season);
        Assert.Equal(1, episode.Episode);
        Assert.Null(episode.PartNumber);
        Assert.Equal("Opening Guest Topic", episode.Subtitle);
    }

    [Fact]
    public void ParseEpisodeInfo_SeparatesYearAndSubtitleAfterSeasonEpisodePattern()
    {
        var episode = MediaNameParser.ParseEpisodeInfo("Show.S01E01.2025.Opening.Guest.Topic.mkv", 0);

        Assert.True(episode.IsTvShow);
        Assert.Equal(1, episode.Season);
        Assert.Equal(1, episode.Episode);
        Assert.Equal("2025", episode.Year);
        Assert.Equal("Opening Guest Topic", episode.Subtitle);
    }

    [Fact]
    public void EpisodeSortKey_OrdersMultipartSegmentsNumerically()
    {
        var sorted = new[]
            {
                "Show.S01E01.part10.mkv",
                "Show.S01E01.part2.mkv",
                "Show.S01E01.part1.mkv"
            }
            .OrderBy(fileName => MediaNameParser.EpisodeSortKey(fileName, 0))
            .ToArray();

        Assert.Equal(
            ["Show.S01E01.part1.mkv", "Show.S01E01.part2.mkv", "Show.S01E01.part10.mkv"],
            sorted);
    }

    [Fact]
    public void ResolvePreferredSeason_PrefersSeasonFromEpisodePattern()
    {
        var season = MediaNameParser.ResolvePreferredSeason(@"D:\Shows\Season 1\Show.S02E03.mkv", "Show.S02E03.mkv");

        Assert.Equal(2, season);
    }

    [Fact]
    public void LibraryLookupTitleBuilder_Build_KeepsManualQueryFirst()
    {
        var titles = LibraryLookupTitleBuilder.Build(
            "Current Title",
            "local",
            @"D:\Movies",
            @"黑客帝国\The.Matrix.Reloaded.2003.mkv",
            "Manual Query");

        Assert.NotEmpty(titles);
        Assert.Equal("Manual Query", titles[0]);
    }

    [Fact]
    public void LibraryLookupTitleBuilder_Build_SplitsForeignAkaAndAddsPureNameFallback()
    {
        var titles = LibraryLookupTitleBuilder.Build(
            "Current Title",
            "local",
            @"D:\Movies",
            @"黑客帝国\The.Matrix.Reloaded.AKA.Matrix.2.2003.mkv");

        Assert.Contains("黑客帝国", titles);
        Assert.Contains("The Matrix Reloaded", titles);
        Assert.Contains("Matrix", titles);
        Assert.Contains(titles, static title =>
            !title.Contains(' ') &&
            title.Contains("TheMatrixReloaded", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryLookupTitleBuilder_Build_UsesSourceTitleBeforeReleaseYear()
    {
        var titles = LibraryLookupTitleBuilder.Build(
            "Current Title",
            "local",
            @"D:\Movies",
            @"American Beauty 1999 Paramount Blu-ray 1080p AVC DTS-HD MA 5.1-blucook#262@CHDBits/American.Beauty.1999.Paramount.Blu-ray.1080p.AVC.DTS-HD.MA.5.1@blucook#262.iso");

        Assert.Contains("American Beauty", titles);
        Assert.DoesNotContain("American Beauty Paramount", titles);
    }

    [Fact]
    public void LibraryMetadataSearchCandidate_ExposesMediaTypeAndOriginalTitle()
    {
        var candidate = new LibraryMetadataSearchCandidate(
            100,
            "tv",
            "黑袍纠察队",
            "superhero satire",
            null,
            "2019-07-26",
            "/poster.jpg",
            null,
            8.7,
            123,
            "The Boys");

        Assert.Equal("剧集", candidate.MediaTypeText);
        Assert.True(candidate.HasOriginalTitle);
        Assert.Equal("The Boys", candidate.OriginalTitleText);
        Assert.Contains("2019-07-26", candidate.MatchMetaText);
    }

    [Fact]
    public void LibraryMetadataSearchCandidate_FormatsMatchedQueryText()
    {
        var candidate = new LibraryMetadataSearchCandidate(
            496243,
            "movie",
            "Parasite",
            "Greed and class discrimination.",
            "2019-05-30",
            null,
            "/parasite.jpg",
            null,
            8.5,
            350,
            "Gisaengchung",
            "\u5BC4\u751F\u866B",
            "\u7236\u76EE\u5F55\u4E2D\u6587\u540D");

        Assert.True(candidate.HasMatchedQuery);
        Assert.Equal("\u7236\u76EE\u5F55\u4E2D\u6587\u540D\uFF1A\u5BC4\u751F\u866B", candidate.MatchedQueryText);
    }
}
