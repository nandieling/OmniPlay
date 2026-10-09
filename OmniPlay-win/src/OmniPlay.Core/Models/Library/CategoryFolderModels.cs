using System.Text.Json;

namespace OmniPlay.Core.Models.Library;

/// <summary>首页分类文件夹（按导演或主演聚合），主键为 <c>kind#name</c>。</summary>
public sealed class CategoryFolder
{
    public required string Id { get; init; }

    /// <summary>分类类型：<c>director</c> 或 <c>cast</c>。</summary>
    public required string Kind { get; init; }

    public required string Name { get; set; }

    /// <summary>用户手选或自动记录的文件夹封面；为空时首页按文件夹内影视随机取。</summary>
    public string? PosterPath { get; set; }

    public double? UpdatedAt { get; set; }

    public static string FolderId(string kind, string name) => kind + "#" + name;

    public string KindLabel => Kind == "director" ? "导演" : "主演";
}

/// <summary>人物档案：头像、TMDB 人物 id 与作品列表缓存。</summary>
public sealed class PersonProfile
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public required string Kind { get; init; }

    public long? TmdbPersonId { get; set; }

    public string? PhotoPath { get; set; }

    /// <summary>作品列表 JSON（<c>List&lt;TmdbFilmographyItem&gt;</c>）。</summary>
    public string? FilmographyJson { get; set; }

    public double? UpdatedAt { get; set; }

    public static string ProfileId(string kind, string name) => kind + "#" + name;

    public List<TmdbFilmographyItem> Filmography
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FilmographyJson))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<TmdbFilmographyItem>>(FilmographyJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public static string EncodeFilmography(IReadOnlyList<TmdbFilmographyItem> items) =>
        JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
}

/// <summary>
/// 文件夹封面编辑器里的一个候选项：该分类下某部影视已经刮削到的海报。
/// </summary>
public sealed class FolderPosterCandidate
{
    public required string PosterPath { get; init; }

    public required string Title { get; init; }

    /// <summary>影视类型标签（电影 / 剧集），显示在候选海报下方。</summary>
    public required string MediaKind { get; init; }

    /// <summary>是否为该文件夹当前使用的封面。</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
/// 文件夹详情页作品年表的一行：年份标签（同年只在第一部前显示）+ 《片名》评分。
/// 库内已有的作品加粗高亮，仅有人物作品记录、库里还没有的排在后面用次级色。
/// </summary>
public sealed class FolderBioRow
{
    public required string YearLabel { get; init; }

    public required string Text { get; init; }

    public bool IsLocal { get; init; }

    public bool IsRemoteOnly => !IsLocal;
}
