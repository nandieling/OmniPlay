using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Entities;

namespace OmniPlay.Infrastructure.Library;

public interface ILocalMetadataSidecarService
{
    LocalSidecarMetadata? ReadMovieMetadata(string videoPath);

    LocalSidecarMetadata? ReadTvShowMetadata(string videoPath);

    LocalSidecarMetadata? ReadEpisodeMetadata(string videoPath);

    Task ExportMovieAsync(
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken = default);

    Task ExportTvShowAsync(
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken = default);

    Task ExportEpisodeThumbnailAsync(
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? thumbnailPath,
        CancellationToken cancellationToken = default);
}

public sealed record LocalSidecarMetadata
{
    public string? Title { get; init; }

    public string? Date { get; init; }

    public string? Overview { get; init; }

    public string? PosterPath { get; init; }

    public string? FanartPath { get; init; }

    public string? ThumbnailPath { get; init; }

    public double? VoteAverage { get; init; }

    public int? TmdbId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>NFO 里的导演名单(<c>&lt;director&gt;</c> 可重复)。</summary>
    public IReadOnlyList<string> Directors { get; init; } = [];

    /// <summary>NFO 里的主演名单(<c>&lt;actor&gt;&lt;name&gt;</c>)。</summary>
    public IReadOnlyList<string> Casts { get; init; } = [];

    public string? DirectorText => Directors.Count > 0 ? string.Join("、", Directors) : null;

    public string? CastText => Casts.Count > 0 ? string.Join("、", Casts) : null;

    public string? EncodedDirectors => Directors.Count > 0 ? PersonNames.Encode(Directors) : null;

    public string? EncodedCasts => Casts.Count > 0 ? PersonNames.Encode(Casts) : null;

    public bool HasAnyValue =>
        !string.IsNullOrWhiteSpace(Title) ||
        !string.IsNullOrWhiteSpace(Date) ||
        !string.IsNullOrWhiteSpace(Overview) ||
        !string.IsNullOrWhiteSpace(PosterPath) ||
        !string.IsNullOrWhiteSpace(FanartPath) ||
        !string.IsNullOrWhiteSpace(ThumbnailPath) ||
        VoteAverage.HasValue ||
        TmdbId.HasValue ||
        SeasonNumber.HasValue ||
        EpisodeNumber.HasValue ||
        Directors.Count > 0 ||
        Casts.Count > 0;
}

public sealed class LocalMetadataSidecarService : ILocalMetadataSidecarService
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly Regex SeasonDirectoryRegex = new(
        @"(?ix)^(season\s*\d+|s\d{1,2}|第\s*\d+\s*季)$",
        RegexOptions.Compiled);

    public LocalSidecarMetadata? ReadMovieMetadata(string videoPath)
    {
        if (!IsUsableLocalPath(videoPath))
        {
            return null;
        }

        var parsed = ReadFirstNfo(BuildMovieNfoCandidates(videoPath)) ?? new LocalSidecarMetadata();
        var posterPath = FindFirstExisting(BuildMoviePosterCandidates(videoPath));
        var fanartPath = FindFirstExisting(BuildMovieFanartCandidates(videoPath));
        var result = parsed with
        {
            PosterPath = posterPath ?? parsed.PosterPath,
            FanartPath = fanartPath ?? parsed.FanartPath
        };

        return result.HasAnyValue ? result : null;
    }

    public LocalSidecarMetadata? ReadTvShowMetadata(string videoPath)
    {
        if (!IsUsableLocalPath(videoPath))
        {
            return null;
        }

        var showDirectory = ResolveTvShowDirectory(videoPath);
        var parsed = ReadFirstNfo([Path.Combine(showDirectory, "tvshow.nfo")]) ?? new LocalSidecarMetadata();
        var posterPath = FindFirstExisting(BuildTvShowPosterCandidates(showDirectory));
        var fanartPath = FindFirstExisting(BuildTvShowFanartCandidates(showDirectory));
        var result = parsed with
        {
            PosterPath = posterPath ?? parsed.PosterPath,
            FanartPath = fanartPath ?? parsed.FanartPath
        };

        return result.HasAnyValue ? result : null;
    }

    public LocalSidecarMetadata? ReadEpisodeMetadata(string videoPath)
    {
        if (!IsUsableLocalPath(videoPath))
        {
            return null;
        }

        var parsed = ReadFirstNfo(BuildEpisodeNfoCandidates(videoPath)) ?? new LocalSidecarMetadata();
        var thumbnailPath = FindFirstExisting(BuildEpisodeThumbnailCandidates(videoPath));
        var result = parsed with
        {
            ThumbnailPath = thumbnailPath ?? parsed.ThumbnailPath
        };

        return result.HasAnyValue ? result : null;
    }

    public async Task ExportMovieAsync(
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var videoPath = ResolveWritableLocalVideoPath(sourceProtocolType, baseUrl, relativePath);
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return;
        }

        var nfoPath = Path.ChangeExtension(videoPath, ".nfo");
        await WriteNfoAsync(nfoPath, "movie", metadata, cancellationToken);
        CopyImage(metadata.PosterPath, ResolveMoviePosterExportPath(videoPath));
    }

    public async Task ExportTvShowAsync(
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var videoPath = ResolveWritableLocalVideoPath(sourceProtocolType, baseUrl, relativePath);
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return;
        }

        var showDirectory = ResolveTvShowDirectory(videoPath);
        await WriteNfoAsync(Path.Combine(showDirectory, "tvshow.nfo"), "tvshow", metadata, cancellationToken);
        CopyImage(metadata.PosterPath, ResolveTvShowPosterExportPath(showDirectory, metadata.PosterPath));
    }

    public async Task ExportEpisodeThumbnailAsync(
        string? sourceProtocolType,
        string? baseUrl,
        string? relativePath,
        string? thumbnailPath,
        CancellationToken cancellationToken = default)
    {
        var videoPath = ResolveWritableLocalVideoPath(sourceProtocolType, baseUrl, relativePath);
        if (string.IsNullOrWhiteSpace(videoPath) ||
            string.IsNullOrWhiteSpace(thumbnailPath) ||
            !File.Exists(thumbnailPath))
        {
            return;
        }

        CopyImage(thumbnailPath, ResolveEpisodeThumbnailExportPath(videoPath, thumbnailPath));

        var parsed = MediaNameParser.ParseEpisodeInfo(Path.GetFileName(videoPath), 0);
        if (!parsed.IsTvShow)
        {
            return;
        }

        var metadata = new LocalSidecarMetadata
        {
            Title = parsed.Subtitle,
            SeasonNumber = parsed.Season,
            EpisodeNumber = parsed.Episode,
            ThumbnailPath = ResolveEpisodeThumbnailExportPath(videoPath, thumbnailPath)
        };
        await WriteNfoAsync(Path.ChangeExtension(videoPath, ".nfo"), "episodedetails", metadata, cancellationToken);
    }

    private static LocalSidecarMetadata? ReadFirstNfo(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var parsed = TryReadNfo(path);
            if (parsed?.HasAnyValue == true)
            {
                return parsed;
            }
        }

        return null;
    }

    private static LocalSidecarMetadata? TryReadNfo(string path)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.None);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            return new LocalSidecarMetadata
            {
                Title = FirstElementValue(root, "title", "showtitle", "originaltitle"),
                Date = FirstElementValue(root, "premiered", "releasedate", "aired", "year"),
                Overview = FirstElementValue(root, "plot", "overview", "outline"),
                VoteAverage = ParseDouble(FirstElementValue(root, "rating", "userrating")),
                TmdbId = ParseInt(ResolveTmdbId(root)),
                SeasonNumber = ParseInt(FirstElementValue(root, "season")),
                EpisodeNumber = ParseInt(FirstElementValue(root, "episode")),
                Directors = PersonNames.Normalize(
                    from element in root.Descendants()
                    where string.Equals(element.Name.LocalName, "director", StringComparison.OrdinalIgnoreCase)
                    select element.Value),
                Casts = PersonNames.Normalize(
                    from actor in root.Descendants()
                    where string.Equals(actor.Name.LocalName, "actor", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(actor.Name.LocalName, "cast", StringComparison.OrdinalIgnoreCase)
                    from name in actor.Elements()
                    where string.Equals(name.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase)
                    select name.Value)
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteNfoAsync(
        string destinationPath,
        string rootName,
        LocalSidecarMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        var root = new XElement(rootName);
        AddElement(root, "title", metadata.Title);
        AddElement(root, rootName == "tvshow" ? "premiered" : "releasedate", metadata.Date);
        AddElement(root, "plot", metadata.Overview);
        foreach (var director in metadata.Directors)
        {
            AddElement(root, "director", director);
        }

        foreach (var actor in metadata.Casts)
        {
            root.Add(new XElement("actor", new XElement("name", actor.Trim())));
        }

        if (metadata.VoteAverage.HasValue)
        {
            AddElement(root, "rating", metadata.VoteAverage.Value.ToString("0.0", CultureInfo.InvariantCulture));
        }

        if (metadata.TmdbId.HasValue)
        {
            root.Add(new XElement(
                "uniqueid",
                new XAttribute("type", "tmdb"),
                new XAttribute("default", "true"),
                metadata.TmdbId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (metadata.SeasonNumber.HasValue)
        {
            AddElement(root, "season", metadata.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (metadata.EpisodeNumber.HasValue)
        {
            AddElement(root, "episode", metadata.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        var xml = $"{document.Declaration}{Environment.NewLine}{document}";
        await File.WriteAllTextAsync(destinationPath, xml, Encoding.UTF8, cancellationToken);
    }

    private static void AddElement(XElement root, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            root.Add(new XElement(name, value.Trim()));
        }
    }

    private static string? FirstElementValue(XElement root, params string[] names)
    {
        foreach (var name in names)
        {
            var element = root
                .Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            var value = element?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ResolveTmdbId(XElement root)
    {
        var uniqueId = root
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "uniqueid", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)element.Attribute("type"), "tmdb", StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim();
        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            return uniqueId;
        }

        return FirstElementValue(root, "tmdbid", "tmdb_id", "id");
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static IEnumerable<string> BuildMovieNfoCandidates(string videoPath)
    {
        yield return Path.ChangeExtension(videoPath, ".nfo");
        var directory = Path.GetDirectoryName(videoPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            yield return Path.Combine(directory, "movie.nfo");
        }
    }

    private static IEnumerable<string> BuildEpisodeNfoCandidates(string videoPath)
    {
        yield return Path.ChangeExtension(videoPath, ".nfo");
    }

    private static IEnumerable<string> BuildMoviePosterCandidates(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
        {
            yield break;
        }

        foreach (var path in BuildImageCandidates(directory, [$"{stem}-poster", stem, "poster", "folder", "cover"]))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> BuildMovieFanartCandidates(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
        {
            yield break;
        }

        foreach (var path in BuildImageCandidates(directory, [$"{stem}-fanart", "fanart", "backdrop"]))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> BuildTvShowPosterCandidates(string showDirectory)
    {
        return BuildImageCandidates(showDirectory, ["poster", "folder", "cover"]);
    }

    private static IEnumerable<string> BuildTvShowFanartCandidates(string showDirectory)
    {
        return BuildImageCandidates(showDirectory, ["fanart", "backdrop"]);
    }

    private static IEnumerable<string> BuildEpisodeThumbnailCandidates(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
        {
            yield break;
        }

        foreach (var path in BuildImageCandidates(directory, [$"{stem}-thumb", $"{stem}-thumbnail", stem]))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> BuildImageCandidates(string directory, IReadOnlyList<string> stems)
    {
        foreach (var stem in stems)
        {
            foreach (var extension in ImageExtensions)
            {
                yield return Path.Combine(directory, $"{stem}{extension}");
            }
        }
    }

    private static string? FindFirstExisting(IEnumerable<string> candidates)
    {
        return candidates.FirstOrDefault(static path => File.Exists(path));
    }

    private static string ResolveTvShowDirectory(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        var directoryName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (SeasonDirectoryRegex.IsMatch(directoryName))
        {
            return Path.GetDirectoryName(directory) ?? directory;
        }

        return directory;
    }

    private static string? ResolveWritableLocalVideoPath(string? sourceProtocolType, string? baseUrl, string? relativePath)
    {
        if (!string.Equals(sourceProtocolType?.Trim(), "local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var videoPath = MediaSourcePathResolver.ResolvePlaybackPath(sourceProtocolType, baseUrl, relativePath);
        return IsUsableLocalPath(videoPath) ? videoPath : null;
    }

    private static bool IsUsableLocalPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               Path.IsPathRooted(path) &&
               File.Exists(path);
    }

    private static string ResolveMoviePosterExportPath(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(directory, $"{stem}-poster.jpg");
    }

    private static string ResolveTvShowPosterExportPath(string showDirectory, string? sourcePath)
    {
        var extension = NormalizeImageExtension(sourcePath);
        return Path.Combine(showDirectory, $"poster{extension}");
    }

    private static string ResolveEpisodeThumbnailExportPath(string videoPath, string? sourcePath)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var extension = NormalizeImageExtension(sourcePath);
        return Path.Combine(directory, $"{stem}-thumb{extension}");
    }

    private static string NormalizeImageExtension(string? sourcePath)
    {
        var extension = string.IsNullOrWhiteSpace(sourcePath) ? ".jpg" : Path.GetExtension(sourcePath);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 8 ? ".jpg" : extension;
    }

    private static void CopyImage(string? sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
        catch
        {
            // Sidecar export should never interrupt library scanning or scraping.
        }
    }
}
