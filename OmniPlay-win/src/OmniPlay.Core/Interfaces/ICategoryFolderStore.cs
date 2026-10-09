using OmniPlay.Core.Models.Library;

namespace OmniPlay.Core.Interfaces;

/// <summary>
/// 首页分类文件夹与人物档案的落地存储，同时负责导演/主演中文译名补全
/// （对齐 mac 版 <c>CategoryFolder</c> + <c>PersonProfile</c> + <c>ChineseNameStore</c>）。
/// </summary>
public interface ICategoryFolderStore
{
    Task<IReadOnlyList<CategoryFolder>> LoadFoldersAsync(
        string kind,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PersonProfile>> LoadProfilesAsync(
        string kind,
        CancellationToken cancellationToken = default);

    /// <summary>把名单里的外文人名换成中文译名（先用手改别名，再查 TMDB），失败时保留原名。</summary>
    Task<TmdbCredits?> LocalizeCreditsAsync(
        TmdbCredits? credits,
        CancellationToken cancellationToken = default);

    /// <summary>刮削拿到演职员后登记文件夹与人物档案。</summary>
    Task RegisterCreditsAsync(
        TmdbCredits? credits,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 用户手改人物名：同步改写影片名单、文件夹与人物档案主键，并记住该名字，
    /// 后续刮削不会退回外文名。返回是否真的改动了数据。
    /// </summary>
    Task<bool> RenamePersonAsync(
        string kind,
        string currentName,
        string newName,
        CancellationToken cancellationToken = default);

    Task SetFolderPosterAsync(
        string kind,
        string name,
        string? posterPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 人物头像的可用本地路径：已下载过就直接返回，只有 TMDB 相对路径时懒下载并写回档案。
    /// 拿不到头像时返回 null，由调用方回退到影视海报。
    /// </summary>
    Task<string?> EnsureProfilePhotoAsync(
        string kind,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>人物作品列表（带中文片名补全，结果缓存进人物档案）。</summary>
    Task<IReadOnlyList<TmdbFilmographyItem>?> FilmographyAsync(
        string kind,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>后台补齐历史库里的外文人名/片名，返回改动的影片数。</summary>
    Task<int> BackfillChineseNamesAsync(CancellationToken cancellationToken = default);
}
