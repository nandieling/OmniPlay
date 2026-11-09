package com.omniplay.tv.phone;

import android.app.Activity;
import android.graphics.Typeface;
import android.text.TextUtils;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.omniplay.tv.data.Models;
import com.omniplay.tv.local.DoubanClient;
import com.omniplay.tv.local.LocalMediaNameParser;
import com.omniplay.tv.local.TmdbClient;
import com.omniplay.tv.ui.Theme;

import java.util.List;

/**
 * 手动匹配与自定义编辑。候选分两栏：TMDB 与豆瓣，都带海报——手机上海报比文字好认。
 * 没填 TMDB Key 时 TMDB 那一栏只剩一句说明，豆瓣照搜照绑（豆瓣两次抓取之间隔 12 秒，
 * 那段时间里会看到「被挡下了」，贴条目链接绑定不吃搜索页）。
 */
public final class PhoneMatchSheet {
    private PhoneMatchSheet() {
    }

    // ---- 手动匹配 ------------------------------------------------------------

    public static void showManualMatch(final PhoneHost host, final Models.LibraryDetail detail) {
        final Activity activity = host.activity();
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "手动匹配",
                detail.title + " · 点中正确的那条即可关联，关联后自动刮削不再覆盖它");
        final LinearLayout body = sheet.body;
        final TextView status = PhoneUi.statusLine(activity, "");

        String[] hint = scrapeHint(detail);
        final EditText keyword = PhoneUi.field(activity, "搜索关键词",
                hint[0] == null ? detail.title : hint[0], false);
        body.addView(keyword, PhoneUi.block(activity, PhoneUi.dp(activity, 8), 0));
        final EditText year = PhoneUi.field(activity, "年份（可空）",
                hint[1] == null ? yearOf(detail.releaseDate) : hint[1], false);
        body.addView(year, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));

        Button search = PhoneUi.primary(activity, "搜索候选");
        body.addView(search, PhoneUi.block(activity, PhoneUi.dp(activity, 12), 0));

        final LinearLayout tmdbSection = new LinearLayout(activity);
        tmdbSection.setOrientation(LinearLayout.VERTICAL);
        body.addView(PhoneUi.section(activity, "TMDB 候选"), PhoneUi.block(activity));
        body.addView(tmdbSection, PhoneUi.block(activity));
        final LinearLayout doubanSection = new LinearLayout(activity);
        doubanSection.setOrientation(LinearLayout.VERTICAL);
        body.addView(PhoneUi.section(activity, "豆瓣候选"), PhoneUi.block(activity));
        body.addView(doubanSection, PhoneUi.block(activity));

        // 豆瓣直接贴链接绑定：搜索被风控时这条路还能走
        final EditText doubanLink = PhoneUi.field(activity, "或贴豆瓣条目链接绑定", "", false);
        body.addView(doubanLink, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 14), 0, 0));
        Button bindLink = PhoneUi.ghost(activity, "用这条链接绑定豆瓣");
        body.addView(bindLink, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));

        search.setOnClickListener(view -> {
            final String query = keyword.getText().toString().trim();
            if (query.isEmpty()) {
                status.setText("先填关键词。");
                return;
            }
            final String typedYear = year.getText().toString().trim();
            status.setText("正在找候选…（豆瓣两次抓取之间隔 12 秒，慢一点是正常的）");
            search.setEnabled(false);
            if (host.engine().tmdbConfigured()) {
                host.io().execute(() -> {
                    try {
                        final List<TmdbClient.Result> results =
                                host.engine().searchCandidates(query, typedYear, detail.itemKind);
                        host.main().post(() -> renderTmdb(host, detail, tmdbSection, results));
                    } catch (Exception error) {
                        host.main().post(() -> renderNote(host, tmdbSection, "TMDB 没找到：" + error.getMessage()));
                    }
                });
            } else {
                renderNote(host, tmdbSection, "没填 TMDB Key，跳过这一栏（设置里可以填）。");
            }
            host.io().execute(() -> {
                try {
                    final List<DoubanClient.Hit> hits = host.engine().searchDoubanCandidates(query);
                    host.main().post(() -> renderDouban(host, detail, doubanSection, hits));
                } catch (Exception error) {
                    host.main().post(() -> renderNote(host, doubanSection, error.getMessage()));
                } finally {
                    host.main().post(() -> {
                        search.setEnabled(true);
                        status.setText("");
                    });
                }
            });
        });

        bindLink.setOnClickListener(view -> {
            String input = doubanLink.getText().toString().trim();
            if (input.isEmpty()) {
                status.setText("先贴一条豆瓣条目链接。");
                return;
            }
            status.setText("正在绑定豆瓣…");
            bind(host, detail, input, status, sheet);
        });

        // 打开就顺手搜一遍，手机上少点一次按钮
        search.performClick();
    }

    private static void bind(final PhoneHost host, final Models.LibraryDetail detail,
                             String input, TextView status, PhoneUi.Sheet sheet) {
        host.io().execute(() -> {
            try {
                host.engine().bindDouban(detail.itemKind, entityIdOf(detail.id), input);
                host.main().post(() -> {
                    sheet.close();
                    host.refreshLibrary();
                    host.toast("已绑定豆瓣");
                });
            } catch (Exception error) {
                host.main().post(() -> status.setText("绑定失败：" + error.getMessage()));
            }
        });
    }

    private static void renderTmdb(PhoneHost host, Models.LibraryDetail detail,
                                   LinearLayout container, List<TmdbClient.Result> results) {
        Activity activity = host.activity();
        container.removeAllViews();
        if (results.isEmpty()) {
            renderNote(host, container, "TMDB 没有匹配的条目，换个关键词试试。");
            return;
        }
        for (final TmdbClient.Result result : results) {
            LinearLayout row = candidateRow(activity, result.title,
                    join(yearOf(result.releaseDate), formatted(result.voteAverage, "TMDB "),
                            result.mediaType),
                    result.basePosterUrl(), host);
            row.setOnClickListener(view -> {
                host.io().execute(() -> {
                    host.engine().bindToTmdbResult(detail.itemKind, entityIdOf(detail.id), result);
                    host.main().post(() -> {
                        host.refreshLibrary();
                        host.toast("已关联 TMDB：" + result.title);
                    });
                });
            });
            container.addView(row);
        }
    }

    private static void renderDouban(PhoneHost host, final Models.LibraryDetail detail,
                                     LinearLayout container, List<DoubanClient.Hit> hits) {
        Activity activity = host.activity();
        container.removeAllViews();
        if (hits.isEmpty()) {
            renderNote(host, container, "豆瓣没有匹配的结果，可以直接贴条目链接。");
            return;
        }
        for (final DoubanClient.Hit hit : hits) {
            LinearLayout row = candidateRow(activity, hit.title,
                    join(yearOf(hit.year == null ? "" : String.valueOf(hit.year)),
                            formatted(hit.rating, "豆瓣 "), hit.mediaType),
                    hit.posterUrl, host);
            row.setOnClickListener(view -> host.io().execute(() -> {
                try {
                    host.engine().bindDouban(detail.itemKind, entityIdOf(detail.id), hit.subjectUrl);
                    host.main().post(() -> {
                        host.refreshLibrary();
                        host.toast("已绑定豆瓣：" + hit.title);
                    });
                } catch (Exception error) {
                    host.toast("绑定失败：" + error.getMessage());
                }
            }));
            container.addView(row);
        }
    }

    /** 候选行：左边海报，右边片名与摘要。手机上认海报比认字符串快得多。 */
    private static LinearLayout candidateRow(Activity activity, String title, String meta,
                                             String posterUrl, PhoneHost host) {
        LinearLayout row = PhoneUi.card(activity, PhoneUi.block(activity, 0, PhoneUi.dp(activity, 10)));
        row.setOrientation(LinearLayout.HORIZONTAL);
        ImageView poster = new ImageView(activity);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        row.addView(poster, PhoneUi.width(PhoneUi.dp(activity, 58), PhoneUi.dp(activity, 84)));
        if (posterUrl != null && !posterUrl.isEmpty()) {
            host.images().load(poster, posterUrl, "", PhoneUi.dp(activity, 58), PhoneUi.dp(activity, 84));
        }
        LinearLayout texts = new LinearLayout(activity);
        texts.setOrientation(LinearLayout.VERTICAL);
        row.addView(texts, PhoneUi.margin(PhoneUi.weight(activity), PhoneUi.dp(activity, 10), 0, 0, 0));
        TextView name = PhoneUi.text(activity, title, 16, Typeface.BOLD, Theme.TEXT);
        name.setSingleLine(true);
        name.setEllipsize(TextUtils.TruncateAt.END);
        texts.addView(name, PhoneUi.block(activity));
        if (!meta.isEmpty()) {
            TextView info = PhoneUi.text(activity, meta, 12, Typeface.NORMAL, Theme.MUTED);
            info.setSingleLine(true);
            info.setEllipsize(TextUtils.TruncateAt.END);
            texts.addView(info, PhoneUi.block(activity, PhoneUi.dp(activity, 2), 0));
        }
        return row;
    }

    private static void renderNote(PhoneHost host, LinearLayout container, String message) {
        container.removeAllViews();
        container.addView(PhoneUi.body(host.activity(), message), PhoneUi.block(host.activity()));
    }

    // ---- 自定义编辑 -----------------------------------------------------------

    public static void showCustomEdit(final PhoneHost host, final Models.LibraryDetail detail) {
        final Activity activity = host.activity();
        final PhoneUi.Sheet sheet = PhoneUi.sheet(activity, "自定义编辑",
                "填过的项会锁住，自动刮削不再覆盖");
        final LinearLayout body = sheet.body;
        final TextView status = PhoneUi.statusLine(activity, "");

        final EditText title = PhoneUi.field(activity, "片名", detail.title, false);
        body.addView(title, PhoneUi.block(activity, PhoneUi.dp(activity, 8), 0));
        final EditText date = PhoneUi.field(activity, "年份或日期", detail.releaseDate, false);
        body.addView(date, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));
        final EditText vote = PhoneUi.field(activity, "评分（0 - 10）",
                detail.voteAverage == null ? "" : String.valueOf(detail.voteAverage), false);
        body.addView(vote, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));
        final EditText genres = PhoneUi.field(activity, "类型（逗号分隔）",
                TextUtils.join("，", detail.genres), false);
        body.addView(genres, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));
        final EditText directors = PhoneUi.field(activity, "导演（逗号分隔）",
                TextUtils.join("，", detail.directors), false);
        body.addView(directors, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));
        final EditText casts = PhoneUi.field(activity, "主演（逗号分隔）",
                TextUtils.join("，", detail.casts), false);
        body.addView(casts, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));

        body.addView(PhoneUi.section(activity, "海报"), PhoneUi.block(activity));
        final LinearLayout preview = new LinearLayout(activity);
        preview.setOrientation(LinearLayout.HORIZONTAL);
        body.addView(preview, PhoneUi.block(activity));
        final ImageView poster = new ImageView(activity);
        poster.setScaleType(ImageView.ScaleType.FIT_CENTER);
        preview.addView(poster, PhoneUi.width(PhoneUi.dp(activity, 96), PhoneUi.dp(activity, 140)));
        host.images().load(poster, host.api().posterUrl(detail.posterAssetId), host.api().cookieHeader(),
                PhoneUi.dp(activity, 96), PhoneUi.dp(activity, 140));
        final EditText posterUrl = PhoneUi.field(activity, "或填一张海报的地址", "", false);
        body.addView(posterUrl, PhoneUi.margin(PhoneUi.block(activity), 0, PhoneUi.dp(activity, 8), 0, 0));
        Button usePoster = PhoneUi.ghost(activity, "用这张海报");
        body.addView(usePoster, PhoneUi.block(activity, PhoneUi.dp(activity, 6), 0));
        usePoster.setOnClickListener(view -> {
            String url = posterUrl.getText().toString().trim();
            if (url.isEmpty()) {
                status.setText("先填海报地址。");
                return;
            }
            host.io().execute(() -> {
                host.engine().library().setPosterOverride(detail.itemKind, entityIdOf(detail.id), url);
                host.main().post(() -> {
                    host.images().load(poster, url, "", PhoneUi.dp(activity, 96), PhoneUi.dp(activity, 140));
                    status.setText("海报已更新。");
                    host.refreshLibrary();
                });
            });
        });

        body.addView(status, PhoneUi.block(activity));
        Button save = PhoneUi.primary(activity, "保存并锁定");
        body.addView(save, PhoneUi.block(activity, PhoneUi.dp(activity, 14), 0));
        save.setOnClickListener(view -> {
            double parsed = 0;
            String typedVote = vote.getText().toString().trim();
            if (!typedVote.isEmpty()) {
                try {
                    parsed = Math.max(0, Math.min(10, Double.parseDouble(typedVote)));
                } catch (NumberFormatException invalid) {
                    status.setText("评分要填 0.0 到 10.0 之间的数字。");
                    return;
                }
            }
            final double finalVote = parsed;
            final String finalTitle = title.getText().toString().trim();
            final String finalDate = date.getText().toString().trim();
            final String finalGenres = toPipeList(genres.getText().toString());
            final String finalDirectors = toPipeList(directors.getText().toString());
            final String finalCasts = toPipeList(casts.getText().toString());
            save.setEnabled(false);
            host.io().execute(() -> {
                host.engine().library().applyCustomEdit(detail.itemKind, entityIdOf(detail.id),
                        finalTitle, finalDate, finalVote, finalGenres, finalDirectors, finalCasts);
                host.main().post(() -> {
                    save.setEnabled(true);
                    sheet.close();
                    host.refreshLibrary();
                    host.toast("资料已保存并锁定");
                });
            });
        });
    }

    // ---- 小工具 ---------------------------------------------------------------

    /** 建议关键词与年份：按源文件名现算，和自动刮削同一口径。 */
    private static String[] scrapeHint(Models.LibraryDetail detail) {
        for (Models.VideoFile file : detail.videoFiles) {
            LocalMediaNameParser.SearchMetadata meta = LocalMediaNameParser
                    .combinedSearchMetadata(file.relativePath, file.fileName);
            if (meta == null) {
                continue;
            }
            // 与自动刮削同一口径：中文名优先，其次父目录中文名、外文名、清洗后的全名
            String query = firstNonEmpty(meta.chineseTitle, meta.parentChineseTitle,
                    meta.foreignTitle, meta.fullCleanTitle);
            if (query != null) {
                return new String[]{query, meta.year == null || meta.year.isEmpty() ? null : meta.year};
            }
        }
        return new String[]{null, null};
    }

    private static String firstNonEmpty(String... values) {
        for (String value : values) {
            if (value != null && !value.trim().isEmpty()) {
                return value.trim();
            }
        }
        return null;
    }

    private static long entityIdOf(String itemId) {
        int colon = itemId.indexOf(':');
        try {
            return Long.parseLong(itemId.substring(colon + 1));
        } catch (RuntimeException ignored) {
            return 0;
        }
    }

    private static String yearOf(String date) {
        if (date == null || date.length() < 4) {
            return "";
        }
        String head = date.substring(0, 4);
        return head.matches("\\d{4}") ? head + " 年" : "";
    }

    private static String formatted(Double value, String prefix) {
        if (value == null || value <= 0) {
            return "";
        }
        return prefix + String.format(java.util.Locale.ROOT, "%.1f", value);
    }

    private static String join(String... parts) {
        StringBuilder builder = new StringBuilder();
        for (String part : parts) {
            if (part == null || part.isEmpty()) {
                continue;
            }
            if (builder.length() > 0) {
                builder.append(" · ");
            }
            builder.append(part);
        }
        return builder.toString();
    }

    private static String toPipeList(String value) {
        String[] items = (value == null ? "" : value).split("[,，、|]");
        StringBuilder builder = new StringBuilder();
        for (String item : items) {
            String trimmed = item.trim();
            if (trimmed.isEmpty()) {
                continue;
            }
            if (builder.length() > 0) {
                builder.append('|');
            }
            builder.append(trimmed);
        }
        return builder.toString();
    }
}
