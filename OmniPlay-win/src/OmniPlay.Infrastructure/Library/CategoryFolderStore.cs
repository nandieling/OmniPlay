using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Models.Library;
using OmniPlay.Core.Runtime;
using OmniPlay.Core.Settings;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.Tmdb;
using System.Collections.Concurrent;

namespace OmniPlay.Infrastructure.Library;

/// <summary>
/// 分类文件夹 + 人物档案 + 中文译名（对齐 mac 版 <c>CategoryFolder</c> /
/// <c>PersonProfile</c> / <c>ChineseNameStore</c>）。
///
/// 关键约束：TMDB 缺少中文条目时会直接回传原文而不是报错，因此"确认无中文译名"
/// 要写成负缓存（30 天过期），而请求失败绝不能当负结果缓存住，否则一次断网会让
/// 外文名永久留在库里。
/// </summary>
public sealed class CategoryFolderStore : ICategoryFolderStore
{
    private const double NegativeCacheTtlSeconds = 30 * 24 * 60 * 60;
    private const int MaxTitleLookupsPerProfile = 20;
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(120);

    /// <summary>作品列表/头像拉取失败后，这段时间内不再重复请求，免得每次打开详情页都去打 API。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>失败退避表，键为 <c>kind#name</c>（作品列表）或 <c>photo#kind#name</c>（人物头像）。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> retryAttempts = new(StringComparer.Ordinal);

    private readonly CategoryFolderRepository folders;
    private readonly ChineseNameCacheRepository cache;
    private readonly ITmdbMetadataClient tmdb;
    private readonly ISettingsService? settingsService;
    private int backfillRunning;

    public CategoryFolderStore(
        CategoryFolderRepository folders,
        ChineseNameCacheRepository cache,
        ITmdbMetadataClient tmdb,
        ISettingsService? settingsService = null)
    {
        this.folders = folders;
        this.cache = cache;
        this.tmdb = tmdb;
        this.settingsService = settingsService;
    }

    public static string PersonKey(int personId) => "person#" + personId;

    public static string WorkKey(string? mediaType, int id) =>
        "work#" + (string.IsNullOrWhiteSpace(mediaType) ? "unknown" : mediaType.ToLowerInvariant()) + "#" + id;

    public static string AliasKey(string kind, string scrapedName) => "alias#" + kind + "#" + scrapedName;

    public Task<IReadOnlyList<CategoryFolder>> LoadFoldersAsync(string kind, CancellationToken cancellationToken = default) =>
        folders.LoadFoldersAsync(kind, cancellationToken);

    public Task<IReadOnlyList<PersonProfile>> LoadProfilesAsync(string kind, CancellationToken cancellationToken = default) =>
        folders.LoadProfilesAsync(kind, cancellationToken);

    public Task SetFolderPosterAsync(string kind, string name, string? posterPath, CancellationToken cancellationToken = default) =>
        folders.SetFolderPosterAsync(kind, name, posterPath, cancellationToken);

    public async Task<TmdbCredits?> LocalizeCreditsAsync(TmdbCredits? credits, CancellationToken cancellationToken = default)
    {
        if (credits is null)
        {
            return null;
        }

        // 用户手改的名字优先，且不依赖网络与凭证。
        var people = credits.Directors.Concat(credits.Casts).ToList();
        var aliasNames = people.Select(person => (Kind: KindOf(credits, person), person.Name)).ToList();
        var aliasKeys = aliasNames
            .Select(pair => AliasKey(pair.Kind, pair.Name))
            .Concat(people.Where(person => person.Id > 0).Select(person => PersonKey(person.Id)))
            .ToList();
        var rows = await cache.LoadAsync(aliasKeys);

        var enabled = await IsAutoTranslationEnabledAsync(cancellationToken);
        var pending = new List<string>();
        if (enabled)
        {
            foreach (var person in people)
            {
                if (person.Id <= 0 || PersonNames.ContainsChinese(person.Name))
                {
                    continue;
                }

                var key = PersonKey(person.Id);
                if (rows.TryGetValue(key, out var existing) && !IsStaleNegative(existing))
                {
                    continue;
                }

                pending.Add(key);
            }
        }

        if (pending.Count > 0)
        {
            var fresh = new List<ChineseNameCacheEntry>();
            var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requested = false;
            foreach (var key in pending.Distinct(StringComparer.Ordinal))
            {
                if (!int.TryParse(key["person#".Length..], out var personId) || personId <= 0)
                {
                    continue;
                }

                if (requested)
                {
                    await Task.Delay(RequestSpacing, cancellationToken);
                }

                var result = await tmdb.FetchChinesePersonNameAsync(personId, cancellationToken);
                requested = true;
                switch (result.Kind)
                {
                    case TmdbChineseNameResultKind.Found:
                        fresh.Add(new ChineseNameCacheEntry { Key = key, Value = result.Name, UpdatedAt = now });
                        break;
                    case TmdbChineseNameResultKind.NotTranslated:
                        // "确认无中文译名"才写负缓存。
                        fresh.Add(new ChineseNameCacheEntry { Key = key, Value = null, UpdatedAt = now });
                        break;
                }
            }

            if (fresh.Count > 0)
            {
                await cache.SaveAsync(fresh, cancellationToken);
                foreach (var entry in fresh)
                {
                    rows[entry.Key] = entry;
                }
            }
        }

        List<TmdbPersonInfo> Translate(IReadOnlyList<TmdbPersonInfo> people, string kind)
        {
            var result = new List<TmdbPersonInfo>(people.Count);
            foreach (var person in people)
            {
                var replacement = ResolveReplacement(person, kind, rows);
                result.Add(replacement is null ? person : person with { Name = replacement });
            }

            return result;
        }

        return new TmdbCredits(Translate(credits.Directors, "director"), Translate(credits.Casts, "cast"));
    }

    public async Task RegisterCreditsAsync(TmdbCredits? credits, CancellationToken cancellationToken = default)
    {
        if (credits is null)
        {
            return;
        }

        var folderPeople = credits.Directors.Select(person => ("director", person.Name))
            .Concat(credits.Casts.Select(person => ("cast", person.Name)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Name))
            .Distinct()
            .ToList();
        await folders.UpsertFoldersAsync(folderPeople, cancellationToken);

        var profiles = credits.Directors.Select(person => ("director", person))
            .Concat(credits.Casts.Select(person => ("cast", person)))
            .Where(pair => pair.person.Id > 0 && !string.IsNullOrWhiteSpace(pair.person.Name))
            // 先记 TMDB 的相对路径，头像真正用到时（打开文件夹详情）再懒下载换成本地文件。
            .Select(pair => (pair.Item1, pair.person.Name, (long)pair.person.Id, pair.person.ProfilePath))
            .Distinct()
            .ToList();
        if (profiles.Count > 0)
        {
            await folders.UpsertProfilesAsync(profiles, cancellationToken);
        }
    }

    /// <summary>用户手改人物名：记别名 + 迁移影片名单/文件夹/人物档案，后续刮削继续沿用新名字。</summary>
    public async Task<bool> RenamePersonAsync(
        string kind,
        string currentName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        currentName = currentName?.Trim() ?? string.Empty;
        newName = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(newName) || string.Equals(currentName, newName, StringComparison.Ordinal))
        {
            return false;
        }

        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entries = new List<ChineseNameCacheEntry>
        {
            new() { Key = AliasKey(kind, currentName), Value = newName, UpdatedAt = now }
        };

        var profile = await folders.FindProfileAsync(kind, currentName, cancellationToken);
        if (profile?.TmdbPersonId is > 0)
        {
            entries.Add(new ChineseNameCacheEntry
            {
                Key = PersonKey((int)profile.TmdbPersonId.Value),
                Value = newName,
                UpdatedAt = now
            });
        }

        await cache.SaveAsync(entries, cancellationToken);
        return await folders.RenamePersonAsync(kind, currentName, newName, cancellationToken);
    }

    public async Task<IReadOnlyList<TmdbFilmographyItem>?> FilmographyAsync(
        string kind,
        string name,
        CancellationToken cancellationToken = default)
    {
        var profile = await folders.FindProfileAsync(kind, name, cancellationToken);
        var cached = profile?.Filmography ?? [];
        var isFresh = profile?.UpdatedAt is double stamp
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp < 7 * 24 * 60 * 60;
        if (cached.Count > 0 && isFresh)
        {
            return cached;
        }

        if (profile?.TmdbPersonId is not > 0)
        {
            return cached.Count > 0 ? cached : null;
        }

        var attemptKey = kind + "#" + name;
        if (retryAttempts.TryGetValue(attemptKey, out DateTimeOffset lastAttempt) &&
            DateTimeOffset.UtcNow - lastAttempt < RetryDelay)
        {
            return cached.Count > 0 ? cached : null;
        }

        var fresh = await tmdb.FetchPersonFilmographyAsync(
            (int)profile.TmdbPersonId.Value,
            kind,
            cancellationToken);
        if (fresh is null || fresh.Count == 0)
        {
            retryAttempts[attemptKey] = DateTimeOffset.UtcNow;
            return cached.Count > 0 ? cached : null;
        }

        retryAttempts.TryRemove(attemptKey, out _);
        var localized = await LocalizeFilmographyTitlesAsync(fresh, cancellationToken);
        await folders.SetProfileFilmographyAsync(kind, name, PersonProfile.EncodeFilmography(localized), cancellationToken);
        return localized;
    }

    public async Task<string?> EnsureProfilePhotoAsync(
        string kind,
        string name,
        CancellationToken cancellationToken = default)
    {
        var profile = await folders.FindProfileAsync(kind, name, cancellationToken);
        var photoPath = profile?.PhotoPath;
        if (IsLocalFile(photoPath))
        {
            return photoPath;
        }

        if (profile?.TmdbPersonId is not > 0)
        {
            return null;
        }

        var personId = (int)profile.TmdbPersonId.Value;
        var attemptKey = "photo#" + kind + "#" + name;
        if (retryAttempts.TryGetValue(attemptKey, out DateTimeOffset lastAttempt) &&
            DateTimeOffset.UtcNow - lastAttempt < RetryDelay)
        {
            return null;
        }

        // 档案里存的是 TMDB 相对路径时直接用它；本地文件被清掉或早期没记路径时补查一次人物详情。
        var remotePath = string.IsNullOrWhiteSpace(photoPath) || Path.IsPathRooted(photoPath)
            ? (await tmdb.FetchPersonDetailAsync(personId, cancellationToken))?.ProfilePath
            : photoPath;
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            retryAttempts[attemptKey] = DateTimeOffset.UtcNow;
            return null;
        }

        var localPath = await tmdb.DownloadProfileImageAsync(remotePath, personId, cancellationToken);
        if (string.IsNullOrWhiteSpace(localPath))
        {
            retryAttempts[attemptKey] = DateTimeOffset.UtcNow;
            return null;
        }

        retryAttempts.TryRemove(attemptKey, out _);
        await folders.SetProfilePhotoAsync(kind, name, localPath, cancellationToken);
        return localPath;
    }

    private static bool IsLocalFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && File.Exists(path);

    public async Task<int> BackfillChineseNamesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref backfillRunning, 1, 0) != 0)
        {
            return 0;
        }

        try
        {
            if (!await IsAutoTranslationEnabledAsync(cancellationToken))
            {
                return 0;
            }

            var changed = 0;
            foreach (var kind in new[] { "director", "cast" })
            {
                var profiles = await folders.LoadProfilesAsync(kind, cancellationToken);
                foreach (var profile in profiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (profile.TmdbPersonId is not > 0)
                    {
                        continue;
                    }

                    var requested = false;
                    if (!PersonNames.ContainsChinese(profile.Name))
                    {
                        var key = PersonKey((int)profile.TmdbPersonId.Value);
                        var known = await cache.LoadAsync(new[] { key });
                        if (!known.TryGetValue(key, out var row) || IsStaleNegative(row))
                        {
                            var result = await tmdb.FetchChinesePersonNameAsync(
                                (int)profile.TmdbPersonId.Value,
                                cancellationToken);
                            requested = true;
                            if (result.Kind == TmdbChineseNameResultKind.Found)
                            {
                                await cache.SaveAsync(new[]
                                {
                                    new ChineseNameCacheEntry
                                    {
                                        Key = key,
                                        Value = result.Name,
                                        UpdatedAt = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                    }
                                }, cancellationToken);
                                if (await folders.RenamePersonAsync(kind, profile.Name, result.Name!, cancellationToken))
                                {
                                    changed++;
                                }
                            }
                            else if (result.Kind == TmdbChineseNameResultKind.NotTranslated)
                            {
                                await cache.SaveAsync(new[]
                                {
                                    new ChineseNameCacheEntry
                                    {
                                        Key = key,
                                        Value = null,
                                        UpdatedAt = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                    }
                                }, cancellationToken);
                            }
                        }
                    }

                    if (profile.Filmography.Count > 0)
                    {
                        var localized = await LocalizeFilmographyTitlesAsync(
                            profile.Filmography,
                            cancellationToken,
                            onlyMissingChinese: true);
                        if (requested)
                        {
                            await Task.Delay(RequestSpacing, cancellationToken);
                        }

                        if (!ReferenceEquals(localized, profile.Filmography) &&
                            SequenceDiffers(localized, profile.Filmography))
                        {
                            await folders.SetProfileFilmographyAsync(
                                kind,
                                profile.Name,
                                PersonProfile.EncodeFilmography(localized),
                                cancellationToken);
                            changed++;
                        }
                    }
                }
            }

            return changed;
        }
        finally
        {
            Interlocked.Exchange(ref backfillRunning, 0);
        }
    }

    /// <summary>
    /// 作品列表里的外文片名改查 TMDB 官方别名表；单个档案最多补 20 条，
    /// 拿不到中文时保留原名（不写负缓存以外的状态）。
    /// </summary>
    private async Task<IReadOnlyList<TmdbFilmographyItem>> LocalizeFilmographyTitlesAsync(
        IReadOnlyList<TmdbFilmographyItem> items,
        CancellationToken cancellationToken,
        bool onlyMissingChinese = true)
    {
        if (!await IsAutoTranslationEnabledAsync(cancellationToken))
        {
            return items;
        }

        var targets = items
            .Where(item => item.TmdbId > 0 &&
                           (!onlyMissingChinese || !PersonNames.ContainsChinese(item.Title)))
            .Take(MaxTitleLookupsPerProfile)
            .ToList();
        if (targets.Count == 0)
        {
            return items;
        }

        var keys = targets.Select(item => WorkKey(item.MediaType, item.TmdbId!.Value)).ToList();
        var rows = await cache.LoadAsync(keys);
        var fresh = new List<ChineseNameCacheEntry>();
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requested = false;
        var changed = false;

        foreach (var item in targets)
        {
            var key = WorkKey(item.MediaType, item.TmdbId!.Value);
            if (rows.TryGetValue(key, out var known))
            {
                if (IsStaleNegative(known))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(known.Value))
                {
                    item.Title = known.Value;
                }

                continue;
            }

            if (requested)
            {
                await Task.Delay(RequestSpacing, cancellationToken);
            }

            var result = await tmdb.FetchChineseTitleAsync(item.MediaType, item.TmdbId.Value, cancellationToken);
            requested = true;
            if (result.Kind == TmdbChineseNameResultKind.Found)
            {
                fresh.Add(new ChineseNameCacheEntry { Key = key, Value = result.Name, UpdatedAt = now });
                item.Title = result.Name!;
                changed = true;
            }
            else if (result.Kind == TmdbChineseNameResultKind.NotTranslated)
            {
                fresh.Add(new ChineseNameCacheEntry { Key = key, Value = null, UpdatedAt = now });
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (fresh.Count > 0)
        {
            await cache.SaveAsync(fresh, cancellationToken);
        }

        _ = changed;
        return items;
    }

    private static string KindOf(TmdbCredits credits, TmdbPersonInfo person) =>
        credits.Directors.Contains(person) ? "director" : "cast";

    private static string? ResolveReplacement(
        TmdbPersonInfo person,
        string kind,
        IReadOnlyDictionary<string, ChineseNameCacheEntry> rows)
    {
        if (rows.TryGetValue(AliasKey(kind, person.Name), out var alias) && !string.IsNullOrWhiteSpace(alias.Value))
        {
            return alias.Value;
        }

        if (person.Id > 0 &&
            rows.TryGetValue(PersonKey(person.Id), out var cached) &&
            !string.IsNullOrWhiteSpace(cached.Value))
        {
            return cached.Value;
        }

        return null;
    }

    private static bool IsStaleNegative(ChineseNameCacheEntry entry)
    {
        if (entry.Value is not null)
        {
            // 用户手改的别名与已确认的中文名都不过期。
            return false;
        }

        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - entry.UpdatedAt;
        return age > NegativeCacheTtlSeconds;
    }

    private static bool SequenceDiffers(
        IReadOnlyList<TmdbFilmographyItem> left,
        IReadOnlyList<TmdbFilmographyItem> right)
    {
        if (left.Count != right.Count)
        {
            return true;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Title, right[index].Title, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsAutoTranslationEnabledAsync(CancellationToken cancellationToken)
    {
        if (Locale.IsEnglish)
        {
            return false;
        }

        if (settingsService is null)
        {
            // 无设置服务时（单元测试直接构造）按"允许"处理，由调用方保证已配置凭证。
            return true;
        }

        AppSettings settings;
        try
        {
            settings = await settingsService.LoadAsync(cancellationToken);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.Equals(settings.AppLanguage, Locale.English, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(settings.Tmdb.CustomApiKey) ||
               !string.IsNullOrWhiteSpace(settings.Tmdb.CustomAccessToken);
    }
}
