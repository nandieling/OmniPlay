package com.omniplay.tv.local;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Port of OmniPlay (mac) MediaNameParser: parses season/episode info and
 * extracts search metadata (Chinese title, foreign title, year) from media file paths.
 */
public final class LocalMediaNameParser {
    private static final Pattern HAN = Pattern.compile("\\p{IsHan}");

    /** 片名里有没有汉字：整条刮削链路都靠它判断「这条还需不需要补中文译名」。 */
    public static boolean containsChinese(String value) {
        return value != null && HAN.matcher(value).find();
    }
    private static final Pattern SEASON_EP = Pattern.compile("(?i)\\b[sS](\\d{1,2})[ ._-]*[eE][pP]?(\\d{1,3})\\b");
    private static final Pattern SEASON_ONLY = Pattern.compile("(?i)\\b[sS](\\d{1,2})\\b");
    private static final Pattern EPISODE_ONLY = Pattern.compile("(?i)\\b[eE][pP]?(\\d{1,3})\\b");
    private static final Pattern ZH_EPISODE = Pattern.compile("第\\s*(\\d{1,3})\\s*[集话]");
    private static final Pattern SEASON_DIR = Pattern.compile("(?i)^season[ ._-]*(\\d{1,2})$");
    private static final Pattern SEASON_ANY = Pattern.compile("(?i)\\bseason[ ._-]*(\\d{1,2})\\b");
    private static final Pattern YEAR_PATTERN = Pattern.compile("(?<![0-9])((19|20)\\d{2})(?![0-9])");

    public static final class EpisodeInfo {
        public final int season;
        public final int episode;
        public final boolean isTvShow;

        EpisodeInfo(int season, int episode, boolean isTvShow) {
            this.season = season;
            this.episode = episode;
            this.isTvShow = isTvShow;
        }
    }

    public static final class SearchMetadata {
        public final String chineseTitle;
        /** 父目录里的中文片名：文件名不带中文、中文名只写在目录上时靠它兜底。 */
        public final String parentChineseTitle;
        public final String foreignTitle;
        public final String fullCleanTitle;
        public final String year;

        SearchMetadata(String chineseTitle, String parentChineseTitle, String foreignTitle,
                       String fullCleanTitle, String year) {
            this.chineseTitle = chineseTitle;
            this.parentChineseTitle = parentChineseTitle;
            this.foreignTitle = foreignTitle;
            this.fullCleanTitle = fullCleanTitle;
            this.year = year;
        }
    }

    private LocalMediaNameParser() {
    }

    public static boolean isVideoFile(String name) {
        String lower = name.toLowerCase(Locale.ROOT);
        return lower.endsWith(".mkv") || lower.endsWith(".mp4") || lower.endsWith(".ts")
                || lower.endsWith(".m2ts") || lower.endsWith(".m2t") || lower.endsWith(".avi")
                || lower.endsWith(".mov") || lower.endsWith(".m4v") || lower.endsWith(".mpg")
                || lower.endsWith(".mpeg") || lower.endsWith(".webm") || lower.endsWith(".wmv")
                || lower.endsWith(".flv") || lower.endsWith(".iso");
    }

    /** Parses S/E from the path; returns season 0 + fallback episode when no markers exist. */
    public static EpisodeInfo parseEpisode(String rawPath, int fallbackIndex) {
        String normalized = rawPath.replace('\\', '/');
        String[] parts = normalized.split("/");
        String fileStem = stripExtension(parts[parts.length - 1]);
        String dirHint = String.join(" ", java.util.Arrays.copyOfRange(parts, 0, parts.length - 1));

        int season = 0;
        int episode = Math.max(1, fallbackIndex);
        boolean fromFile = false;
        // 文件名里真的写了 Sxx（含 S00）才算「季是明确的」：S00E01 是特别篇，
        // 不能跟「压根没写季」一样被兜底成第 1 季。
        boolean explicitSeason = false;

        Matcher m = SEASON_EP.matcher(fileStem);
        if (m.find()) {
            season = parseInt(m.group(1), 0);
            episode = parseInt(m.group(2), 1);
            fromFile = true;
            explicitSeason = true;
        } else {
            m = ZH_EPISODE.matcher(fileStem);
            if (m.find()) {
                episode = parseInt(m.group(1), 1);
                fromFile = true;
            } else {
                m = EPISODE_ONLY.matcher(fileStem);
                if (m.find() && hasSeasonContext(dirHint, fileStem)) {
                    episode = parseInt(m.group(1), 1);
                    fromFile = true;
                }
            }
        }

        int dirSeason = seasonFromPathParts(parts, fileStem);
        if (dirSeason > 0) {
            season = dirSeason;
        } else if (season == 0) {
            Matcher sm = SEASON_EP.matcher(dirHint);
            if (sm.find()) {
                season = parseInt(sm.group(1), 0);
                explicitSeason = true;
            }
        }

        if (season == 0 && !fromFile && !hasSeasonContext(dirHint, fileStem)) {
            return new EpisodeInfo(0, episode, false);
        }
        if (season == 0 && !explicitSeason) {
            season = 1;
        }
        return new EpisodeInfo(season, episode, true);
    }

    private static int seasonFromPathParts(String[] parts, String fileStem) {
        String lowerStem = fileStem.toLowerCase(Locale.ROOT);
        for (int i = 0; i < parts.length - 1; i++) {
            String part = parts[i];
            if (part.equalsIgnoreCase("BDMV") || part.equalsIgnoreCase("STREAM")
                    || isGenericFolder(part)) {
                continue;
            }
            Matcher dm = SEASON_DIR.matcher(stripExtension(part));
            if (dm.find()) {
                return parseInt(dm.group(1), 0);
            }
            Matcher am = SEASON_ANY.matcher(part);
            if (am.find() && lowerStem.matches(".*[eE]\\d.*|.*第\\s*\\d+\\s*[集话].*")) {
                return parseInt(am.group(1), 0);
            }
        }
        return 0;
    }

    private static boolean hasSeasonContext(String dirHint, String fileStem) {
        return SEASON_ANY.matcher(dirHint).find() || SEASON_EP.matcher(fileStem).find()
                || SEASON_ONLY.matcher(fileStem).find() || ZH_EPISODE.matcher(fileStem).find();
    }

    private static boolean isGenericFolder(String name) {
        String lower = name.toLowerCase(Locale.ROOT);
        return lower.startsWith("disc ") || lower.startsWith("dvd") || lower.startsWith("cd ")
                || lower.equals("disc") || lower.matches("(?i)(volume|vol)\\s*\\d+")
                || lower.equals("bd") || lower.equals("cd");
    }

    /**
     * Source text used for scraping: BDMV parent folder, season-range package folder,
     * or file stem (plus Chinese parent folder name when the stem has no Chinese).
     */
    public static String cleanedTitleSource(String rawPath) {
        String normalized = rawPath.replace('\\', '/');
        String[] parts = normalized.split("/");
        String lower = normalized.toLowerCase(Locale.ROOT);
        if (lower.contains("/bdmv/") || lower.endsWith(".m2ts") || lower.endsWith(".m2t")) {
            for (int i = 0; i < parts.length; i++) {
                if (parts[i].equalsIgnoreCase("BDMV") && i > 0) {
                    String candidate = parts[i - 1];
                    if (isGenericFolder(candidate) && i > 1) {
                        candidate = parts[i - 2];
                    }
                    return candidate;
                }
            }
        }
        String fileStem = stripExtension(parts[parts.length - 1]);
        if (parts.length >= 2) {
            String parent = parts[parts.length - 2];
            if (!HAN.matcher(fileStem).find() && HAN.matcher(parent).find()
                    && !parent.equalsIgnoreCase("BDMV") && !parent.equalsIgnoreCase("STREAM")) {
                return parent + " " + fileStem;
            }
        }
        return fileStem;
    }

    /**
     * 先把命名里的全角标点与罗马数字字符折成 ASCII。日文、中文资源名里满是「／」「：」
     * 「「」」这类字符：它们既不算字母也不算数字，整段词会被当成垃圾丢掉，
     * 「Fate／stay night」就只剩 "night"；「Ⅱ.lost butterfly」的 Ⅱ 是罗马数字字符，
     * 同样会被丢掉，片名就断在「劇場版」上。折一遍再走下面的清洗规则。
     */
    static String normalizeNameText(String raw) {
        if (raw == null || raw.isEmpty()) {
            return "";
        }
        String[] roman = {"I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X"};
        StringBuilder out = new StringBuilder(raw.length());
        for (int index = 0; index < raw.length(); index++) {
            char c = raw.charAt(index);
            if (c == '\uFF0F' || c == '\uFF1A' || c == '\uFF1B' || c == '\uFF0C'
                    || c == '\u3001' || c == '\u3002' || c == '\u30FB' || c == '\u3000'
                    || c == '\uFF5E' || c == '\uFF5C' || c == '\u00B7' || c == '\u300C'
                    || c == '\u300D' || c == '\u300E' || c == '\u300F') {
                out.append(' ');
            } else if (c >= '\u2160' && c <= '\u2169') {
                out.append(roman[c - '\u2160']);
            } else if (c >= '\u2170' && c <= '\u2179') {
                out.append(roman[c - '\u2170']);
            } else if (c >= '\uFF10' && c <= '\uFF19') {
                out.append((char) ('0' + (c - '\uFF10')));
            } else if (c >= '\uFF21' && c <= '\uFF3A') {
                out.append((char) ('A' + (c - '\uFF21')));
            } else if (c >= '\uFF41' && c <= '\uFF5A') {
                out.append((char) ('a' + (c - '\uFF41')));
            } else {
                out.append(c);
            }
        }
        return out.toString();
    }

    public static SearchMetadata extractSearchMetadata(String rawPath) {
        // 全角标点、罗马数字先折成 ASCII，父目录名一并折：见 normalizeNameText。
        String normalizedPath = normalizeNameText(rawPath);
        String originalText = cleanedTitleSource(normalizedPath);
        // 照 mac 版先来一刀：标题之后的 1080p / BluRay / x265 这些发布元信息整段丢掉。
        String text = truncateBeforeReleaseMetadata(originalText)
                .replaceAll("[\\[\\](){}\u3010\u3011\uFF08\uFF09\u300A\u300B\u300C\u300D\u300E\u300F\u3014\u3015\u3016\u3017]", " ");

        String year = chooseReleaseYear(originalText);
        if (year == null) {
            year = chooseReleaseYear(text);
        }
        if (year != null) {
            // 年份同样按 mac 版的做法截断，不是全局删除：年份往后都是发布组信息。
            Matcher yearMatch = Pattern.compile(
                    "[ .\\-_()\\[\\]{}\u3010\u3011\uFF08\uFF09\u300A\u300B\u300C\u300D\u300E\u300F\u3014\u3015\u3016\u3017]*"
                            + year
                            + "[ .\\-_()\\[\\]{}\u3010\u3011\uFF08\uFF09\u300A\u300B\u300C\u300D\u300E\u300F\u3014\u3015\u3016\u3017]*")
                    .matcher(text);
            if (yearMatch.find()) {
                text = text.substring(0, yearMatch.start());
            }
        }

        String[] cleanPatterns = {
                "(?i)\\b(1080p|2160p|4k|720p|480p|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip"
                        + "|remux|x264|x265|h\\.?264|h\\.?265|hevc|avc|vc[- ]?1|aac|dts[- ]?hd|dts"
                        + "|lpcm|truehd|hdr|dv)\\b",
                "(?i)\\b[sS]\\d{1,2}\\s*[-–~]\\s*(?:[sS])?\\d{1,2}\\b",
                "(?i)\\bseason\\s*\\d{1,2}\\s*[-–~]\\s*(?:season\\s*)?\\d{1,2}\\b",
                "(?i)\\b[sS]\\d{1,2}[eE][pP]?\\d{1,3}\\b",
                "(?i)\\b[sS]\\d{1,2}\\b",
                "(?i)\\b[eE][pP]?\\d{1,3}\\b",
                "第\\s*\\d{1,3}\\s*[集话]",
                "(?i)\\bseason\\s*\\d{1,2}\\b",
                "(?i)\\b\\d{1,2}bit\\b",
                "(?i)\\b(aac|ac3|eac3|ddp|dts|dts[- ]?hd|truehd|lpcm|flac|mp3)\\d*(\\.\\d+)?\\b",
                "(?i)\\b(ma|hi10p|10bit|8bit)\\b",
                "(?i)\\b(usa|ger|gbr|uk|jpn|jap|kor|chn|hkg|tw|fr|fra|ita|esp|rus|can|aus)\\b",
                "(?i)\\b(bonus|extras?|featurette|behind[- ]?the[- ]?scenes|trailer|sample)\\b",
                "(?i)\\b(disc|disk|cd|dvd)\\b",
                "(?i)\\b(disc|disk|cd|dvd)\\s*[-_ ]?\\d{1,2}\\b",
                "(?i)\\b(bdrom|bdmv)\\b",
                "(?i)\\b(vol|volume)\\s*[-_ ]?\\d{1,2}([–—-]\\d{1,2})?\\b",
                "\\d{1,3}\\s*周年\\s*纪念版",
                "(?i)\\b(cctv\\d*k?|cmctv)\\b",
                "(映画|剧场版|劇場版|電影版|电影版|完全版|总集篇|總集篇|特別篇|特别篇)",
                "(?i)\\b(special\\s*features?|featurettes?)\\b",
                "(?i)\\b\\d{1,3}(st|nd|rd|th)\\s+anniversary(\\s+edition)?\\b",
                "(?i)\\b(anniversary|edition)\\b",
        };
        for (String pattern : cleanPatterns) {
            text = text.replaceAll(pattern, " ");
        }
        text = text.replaceAll("[._]+", " ")
                .replaceAll("\\[[^\\]]*\\]", " ")
                .replaceAll("\\([^)]*\\)", " ")
                .replaceAll("[\u3010\u3011\uFF08\uFF09\u300A\u300B\u300C\u300D\u300E\u300F\u3014\u3015\u3016\u3017]", " ")
                .replaceAll("\\s+", " ")
                .trim();

        List<String> tokens = splitTokens(text);
        String chineseTitle = extractChineseTitle(tokens);
        if (chineseTitle == null) {
            // 清洗把中文也削掉时，退回未清洗的原始命名再找一次中文片名。
            chineseTitle = extractChineseTitle(titleTokens(originalText));
        }
        String foreignTitle = extractForeignTitle(tokens);
        return new SearchMetadata(
                chineseTitle == null || chineseTitle.isEmpty() ? null : chineseTitle,
                null,
                foreignTitle,
                text.isEmpty() ? null : text,
                year);
    }

    /**
     * 挭平「相对路径」与「单独的文件名」两个来源的刮前信息（mac 版
     * combinedSearchMetadata）。刮前阶段应该用这个，而不是扫描时存下的那一份快照：
     * 快照是别的版本的清洗规则算出来的，年份与副查询词会陈旧，年份一旦为空，
     * 候选打分里的年份权重整段失效，热度最高的同名新作就会把正确的老片抢掉。
     */
    public static SearchMetadata combinedSearchMetadata(String relativePath, String fileName) {
        String path = relativePath == null ? "" : relativePath;
        String name = fileName == null ? "" : fileName;
        // Jellyfin / Emby 这类媒体服务器的路径里只有 items/12345/download，信息全在文件名上。
        boolean fileNameFirst = isLikelyMediaServerEndpointPath(path);
        String primaryRaw = fileNameFirst ? name : path;
        String secondaryRaw = fileNameFirst ? path : name;
        SearchMetadata primary = extractSearchMetadata(
                primaryRaw.isEmpty() ? secondaryRaw : primaryRaw);
        SearchMetadata secondary = primaryRaw.equals(secondaryRaw)
                ? primary : extractSearchMetadata(secondaryRaw);
        String parentChinese = fileNameFirst ? null : parentChineseTitle(path);
        return new SearchMetadata(
                firstNonEmpty(primary.chineseTitle, secondary.chineseTitle),
                firstNonEmpty(parentChinese, null),
                firstNonEmpty(primary.foreignTitle, secondary.foreignTitle),
                firstNonEmpty(primary.fullCleanTitle, secondary.fullCleanTitle),
                firstNonEmpty(primary.year, secondary.year));
    }

    /** 路径像 Jellyfin / Emby 的下载接口，而不是真实的文件树。 */
    static boolean isLikelyMediaServerEndpointPath(String relativePath) {
        if (relativePath == null) {
            return false;
        }
        String normalized = relativePath.replace('\'', '/').replaceAll("^/+", "")
                .toLowerCase(Locale.ROOT);
        return normalized.matches("^(items/[^/]+/download|library/parts/|library/metadata/"
                + "|video/|videos/).*");
    }

    /**
     * mac 版 bestDisplayTitle 的等价物：中文片名、父目录中文名、外文片名、清洗后的整串，一层层
     * 往下退，每一层都先过一遍「这名字还能看吗」。全部不合格时返回 null，由调用方兜底。
     */
    public static String displayTitle(String relativePath, String fileName) {
        SearchMetadata primary = extractSearchMetadata(
                relativePath == null || relativePath.isEmpty() ? fileName : relativePath);
        SearchMetadata secondary = primary;
        if (fileName != null && !fileName.equals(relativePath)) {
            secondary = extractSearchMetadata(fileName);
        }
        String[] candidates = {
                firstNonEmpty(primary.chineseTitle, secondary.chineseTitle),
                parentChineseTitle(relativePath),
                firstNonEmpty(primary.foreignTitle, secondary.foreignTitle),
                firstNonEmpty(primary.fullCleanTitle, secondary.fullCleanTitle)
        };
        for (String candidate : candidates) {
            String usable = usableDisplayTitle(candidate);
            if (usable != null) {
                return usable;
            }
        }
        return null;
    }

    /** 文件名是英文或编号时，mac 版会拿父目录里的中文名当片名。 */
    public static String parentChineseTitle(String rawPath) {
        if (rawPath == null) {
            return null;
        }
        String normalized = rawPath.replace('\\', '/');
        int slash = normalized.lastIndexOf('/');
        if (slash <= 0) {
            return null;
        }
        String parentPath = normalized.substring(0, slash);
        int parentSlash = parentPath.lastIndexOf('/');
        String parentName = (parentSlash >= 0 ? parentPath.substring(parentSlash + 1) : parentPath).trim();
        if (parentName.isEmpty() || !HAN.matcher(parentName).find()) {
            return null;
        }
        Matcher bracketed = Pattern.compile("[\u3010\uFF08\u300A]([^\u3011\uFF09\u300B]{2,})[\u3011\uFF09\u300B]")
                .matcher(parentName);
        if (bracketed.find() && HAN.matcher(bracketed.group(1)).find()) {
            return bracketed.group(1).trim();
        }
        return extractSearchMetadata(parentName).chineseTitle;
    }

    private static String firstNonEmpty(String first, String second) {
        return first != null && !first.isEmpty() ? first : second;
    }

    /** 片名得像个片名：纯数字、"movie"、"BDMV" 这类占位词一律判不合格。 */
    public static String usableDisplayTitle(String value) {
        if (value == null) {
            return null;
        }
        String normalized = value.replaceAll("[._]+", " ")
                .replaceAll("\\s+", " ")
                .trim();
        if (normalized.isEmpty()) {
            return null;
        }
        if (BLOCKED_TITLES.contains(normalized.toLowerCase(Locale.ROOT))) {
            return null;
        }
        if (normalized.matches("\\d+")) {
            // mac 版同样只拦「全零」和两位数：《2012》这种纯数字片名要放过去。
            if (normalized.length() < 3 || normalized.replace("0", "").isEmpty()) {
                return null;
            }
        }
        return normalized.matches(".*[A-Za-z\\d\\p{IsHan}].*") ? normalized : null;
    }

    private static final Set<String> BLOCKED_TITLES = new HashSet<>(Arrays.asList(
            "download", "downloads", "items", "library", "libraries", "media", "movie", "movies",
            "film", "films", "show", "shows", "series", "tv", "video", "videos", "stream",
            "bdmv", "plex", "emby", "jellyfin", "1080p", "2160p", "4k", "x264", "x265"));

    /** 按发布元信息截断：第一个像发布信息的词之后全部丢掉，这是长命名变短的关键一步。 */
    private static String truncateBeforeReleaseMetadata(String input) {
        String normalized = input.replaceAll("[._]+", " ").replaceAll("\\s+", " ").trim();
        String[] tokens = normalized.isEmpty() ? new String[0] : normalized.split(" ");
        StringBuilder kept = new StringBuilder();
        for (String rawToken : tokens) {
            if (isReleaseMetadataToken(rawToken.toLowerCase(Locale.ROOT))) {
                break;
            }
            if (kept.length() > 0) {
                kept.append(' ');
            }
            kept.append(rawToken);
        }
        return kept.length() == 0 ? normalized : kept.toString();
    }

    private static boolean isReleaseMetadataToken(String token) {
        return token.matches("^[se]\\d{1,3}$")
                || token.matches("^s\\d{1,2}e?\\d{1,3}$")
                || token.matches("^ep?\\d{1,3}$")
                || token.matches("^\\d{3,4}p$")
                || token.matches("^(4k|uhd|hdr|dv|atmos|ddp\\d*(\\.\\d+)?|aac\\d*(\\.\\d+)?|ac3|eac3"
                        + "|dts|dts[- ]?hd|truehd|lpcm|flac|mp3)$")
                || token.matches("^(x|h)?26[45]$")
                || token.matches("^(web|webdl|webrip|bluray|blu-ray|bdrip|remux|avc|vc-?1)$")
                || token.matches("^(amzn|nf|netflix|dsnp|disney|hmax|max|atvp|appletv|hulu|cr)$")
                || token.matches("^(flux|ntb|cakes|tgx|successfulcrab)$")
                || token.matches("^(bonus|extra|extras|featurette|trailer|sample)$")
                || token.matches("^(disc|disk|cd|dvd)([-_ ]?\\d{1,2})?$")
                || token.matches("^(bdrom|bdmv)$")
                || token.matches("^(vol|volume)[-_ ]?\\d{1,2}([\u2013-]\\d{1,2})?$");
    }

    private static final Set<String> GENERIC_CHINESE_TOKENS = new HashSet<>(Arrays.asList(
            "电影", "電影", "电影版", "電影版", "剧场版", "劇場版", "映画", "完全版",
            "总集篇", "總集篇", "特别篇", "特別篇", "花絮", "幕后", "幕後", "特典",
            "附赠", "附贈", "预告片", "預告片", "样片", "樣片"));

    /** 连续的中文词分一组，取最长的那组当片名；「特典」「花絮」之后的补充说明切掉。 */
    private static String extractChineseTitle(List<String> tokens) {
        List<List<String>> groups = new ArrayList<>();
        List<String> current = new ArrayList<>();
        for (String token : tokens) {
            boolean hasChinese = HAN.matcher(token).find();
            boolean generic = GENERIC_CHINESE_TOKENS.contains(token.trim());
            boolean numericSuffix = !current.isEmpty() && token.matches("\\d+");
            if (hasChinese && !generic) {
                current.add(token);
            } else if (numericSuffix) {
                current.add(token);
            } else if (!current.isEmpty()) {
                groups.add(current);
                current = new ArrayList<>();
            }
        }
        if (!current.isEmpty()) {
            groups.add(current);
        }
        String merged = null;
        for (List<String> group : groups) {
            String joined = String.join("", group).trim();
            if (!joined.isEmpty() && (merged == null || joined.length() > merged.length())) {
                merged = joined;
            }
        }
        return merged == null ? null : trimChineseSupplementTitle(merged);
    }

    private static String trimChineseSupplementTitle(String input) {
        String[] markers = {"\u7279\u5178", "\u7279\u5225\u6536\u9304", "\u7279\u522b\u6536\u9304",
                "\u82b1\u7c64", "\u5e55\u540e", "\u5e55\u5f8c", "\u9644\u8d60", "\u9644\u8d08",
                "\u9884\u544a\u7247", "\u9810\u544a\u7247", "\u6837\u7247", "\u6a23\u7247"};
        for (String marker : markers) {
            int index = input.indexOf(marker);
            if (index < 0) {
                continue;
            }
            String prefix = input.substring(0, index).trim();
            if (prefix.length() >= 2) {
                return prefix;
            }
        }
        return input;
    }

    private static final Set<String> FOREIGN_NOISE_TOKENS = new HashSet<>(Arrays.asList(
            "tx", "mweb", "adweb", "web", "dl", "bluray", "bdrip", "webrip", "proper", "repack",
            "remastered", "extended", "unrated", "vol", "volume", "disc", "disk", "cd", "part",
            "bonus", "extra", "extras", "featurette", "trailer", "sample", "cmctv", "bdrom", "bdmv",
            "special", "features", "anniversary", "edition", "avc", "vc1", "lpcm", "truehd", "ma",
            "usa", "ger", "gbr", "uk", "jpn", "jap", "kor", "chn", "hkg", "tw"));

    /** 外文片名：滤掉发布组、编码、地区代号，只在开头出现的序号（2、3 这种）留在片名里。 */
    private static String extractForeignTitle(List<String> tokens) {
        int leadingNumbers = 0;
        while (leadingNumbers < tokens.size() && tokens.get(leadingNumbers).matches("\\d{1,2}")) {
            leadingNumbers++;
        }
        List<String> kept = new ArrayList<>();
        for (int index = 0; index < tokens.size(); index++) {
            String token = tokens.get(index);
            String lower = token.toLowerCase(Locale.ROOT);
            if (FOREIGN_NOISE_TOKENS.contains(lower) || lower.startsWith("-") || lower.endsWith("-")) {
                continue;
            }
            if (token.matches("\\d+") && !(leadingNumbers >= 2 && index < leadingNumbers)) {
                continue;
            }
            if (token.matches("^(disc|disk|cd|dvd)([-_ ]?\\d{1,2})?$")
                    || token.matches("^(vol|volume)[-_ ]?\\d{1,2}([\u2013-]\\d{1,2})?$")
                    || token.matches("^[se]\\d{1,3}$")
                    || token.matches("^(x|h)?26[45]$")
                    || token.matches("^(aac|ac3|eac3|ddp|dts|truehd|flac|mp3)\\d*(\\.\\d+)?$")
                    || token.matches("^\\d{1,2}bit$")
                    || token.matches("^cctv\\d*k?$")) {
                continue;
            }
            if (HAN.matcher(token).find() || !token.matches("[\\p{L}0-9'&:+-]+")) {
                continue;
            }
            kept.add(token);
        }
        String merged = trimForeignSupplementTitle(String.join(" ", kept).trim());
        if (merged.isEmpty() || merged.toLowerCase(Locale.ROOT).matches("^(vol|volume|disc|disk|cd|part)\\s*\\d*$")) {
            return null;
        }
        return merged;
    }

    private static String trimForeignSupplementTitle(String input) {
        return input.replaceAll("(?i)\s+(the\s+movie|main\s+feature|feature\s+film)$", "").trim();
    }

    private static List<String> titleTokens(String value) {
        return splitTokens(value.replaceAll("[._]+", " ")
                .replaceAll("\\[[^\\]]*\\]|\\([^)]*\\)", " ")
                .replaceAll("[\u3010\u3011\uFF08\uFF09\u300A\u300B\u300C\u300D\u300E\u300F\u3014\u3015\u3016\u3017]", " ")
                .replaceAll("\\s+", " ")
                .trim());
    }

    private static List<String> splitTokens(String value) {
        return value.isEmpty() ? new ArrayList<String>() : Arrays.asList(value.split(" "));
    }

    public static String chooseReleaseYear(String text) {
        List<String> candidates = yearCandidates(text);
        if (candidates.isEmpty()) {
            return null;
        }
        if (candidates.size() >= 2) {
            // 「2012.2009.1080p」这种写法里第一个四位数字是片名，文件名又是从片名开头写的：
            // 开头就是第一个候选时，年份要取第二个。mac 版同一判法（compactPrefix.hasPrefix）。
            String first = candidates.get(0);
            String second = candidates.get(1);
            String compactPrefix = text.substring(0, Math.min(text.length(), 12))
                    .replaceAll("[.\\-_\\s/]+", "");
            if (compactPrefix.startsWith(first)) {
                return second;
            }
        }
        return candidates.get(0);
    }

    /**
     * mac 版 extractYearCandidates：只收前后都不接数字的 1900-2099。上一版这里取的是「最大的
     * 那个」，于是 Toy.Story.1995.2010.720p 记成 2010、按年份建的文件夹（2024/某片.2019.mkv）
     * 记成 2024——片名里自带的年份反而永远抢不过后面那些重制 / 发行年份。
     */
    private static List<String> yearCandidates(String text) {
        List<String> years = new ArrayList<>();
        if (text == null || text.isEmpty()) {
            return years;
        }
        Matcher m = YEAR_PATTERN.matcher(text);
        while (m.find()) {
            // group(1) 就是那四位数字：前后都不接数字才算年份，12010 里那截不算。
            int value = Integer.parseInt(m.group(1));
            if (value >= 1900 && value <= 2099) {
                years.add(String.valueOf(value));
            }
        }
        return years;
    }

    /** Folder that groups a TV series' files (the show-level folder). */
    public static String showGroupFolder(String rawPath) {
        String normalized = rawPath.replace('\\', '/');
        String[] parts = normalized.split("/");
        for (int i = parts.length - 1; i >= 0; i--) {
            String part = parts[i];
            Matcher dm = SEASON_DIR.matcher(stripExtension(part));
            if (dm.find() || part.equalsIgnoreCase("BDMV") || part.equalsIgnoreCase("STREAM")
                    || isGenericFolder(part)) {
                continue;
            }
            return part;
        }
        return stripExtension(parts[parts.length - 1]);
    }

    public static boolean isTvLikePath(String rawPath) {
        return SEASON_EP.matcher(rawPath).find()
                || SEASON_ANY.matcher(rawPath).find()
                || ZH_EPISODE.matcher(rawPath).find()
                || seasonFromPathParts(rawPath.replace('\\', '/').split("/"), stripExtension(rawPath)) > 0;
    }

    /** Display name from a bare file name: stem without quality/codec tags. */
    public static String stripDisplay(String fileName) {
        String stem = fileName;
        int dot = fileName.lastIndexOf('.');
        if (dot > 0) {
            stem = fileName.substring(0, dot);
        }
        String text = stem
                .replaceAll("(?i)\\b(1080p|2160p|4k|720p|480p|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h[.]?264|h[.]?265|hevc|avc|hdr10?|dv)\\b", " ")
                .replaceAll("(?i)\\b[sS]\\d{1,2}[eE][pP]?\\d{1,3}\\b", " ")
                .replaceAll("(?i)\\b[sS]\\d{1,2}\\b", " ")
                .replaceAll("[._-]+", " ")
                .replaceAll("\\[[^\\]]*\\]", " ")
                .replaceAll("\\([^)]*\\)", " ")
                .replaceAll("\\s+", " ")
                .trim();
        return text.isEmpty() ? stem : text;
    }

    private static String stripExtension(String name) {
        int dot = name.lastIndexOf('.');
        return dot > 0 ? name.substring(0, dot) : name;
    }

    private static int parseInt(String value, int fallback) {
        try {
            return Integer.parseInt(value);
        } catch (NumberFormatException ignored) {
            return fallback;
        }
    }
}
