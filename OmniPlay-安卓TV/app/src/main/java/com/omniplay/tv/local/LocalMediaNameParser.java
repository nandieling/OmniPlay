package com.omniplay.tv.local;

import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Port of OmniPlay (mac) MediaNameParser: parses season/episode info and
 * extracts search metadata (Chinese title, foreign title, year) from media file paths.
 */
public final class LocalMediaNameParser {
    private static final Pattern HAN = Pattern.compile("\\p{Han}");
    private static final Pattern SEASON_EP = Pattern.compile("(?i)\\b[sS](\\d{1,2})[ ._-]*[eE][pP]?(\\d{1,3})\\b");
    private static final Pattern SEASON_ONLY = Pattern.compile("(?i)\\b[sS](\\d{1,2})\\b");
    private static final Pattern EPISODE_ONLY = Pattern.compile("(?i)\\b[eE][pP]?(\\d{1,3})\\b");
    private static final Pattern ZH_EPISODE = Pattern.compile("第\\s*(\\d{1,3})\\s*[集话]");
    private static final Pattern SEASON_DIR = Pattern.compile("(?i)^season[ ._-]*(\\d{1,2})$");
    private static final Pattern SEASON_ANY = Pattern.compile("(?i)\\bseason[ ._-]*(\\d{1,2})\\b");
    private static final Pattern YEAR_PATTERN = Pattern.compile("(?<![0-9])(19|20)\\d{2}(?![0-9])");

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
        public final String foreignTitle;
        public final String fullCleanTitle;
        public final String year;

        SearchMetadata(String chineseTitle, String foreignTitle, String fullCleanTitle, String year) {
            this.chineseTitle = chineseTitle;
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

        Matcher m = SEASON_EP.matcher(fileStem);
        if (m.find()) {
            season = parseInt(m.group(1), 0);
            episode = parseInt(m.group(2), 1);
            fromFile = true;
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
            }
        }

        if (season == 0 && !fromFile && !hasSeasonContext(dirHint, fileStem)) {
            return new EpisodeInfo(0, episode, false);
        }
        if (season == 0) {
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

    public static SearchMetadata extractSearchMetadata(String rawPath) {
        String originalText = cleanedTitleSource(rawPath);
        String text = originalText;

        String year = chooseReleaseYear(originalText);
        if (year == null) {
            year = chooseReleaseYear(text);
        }
        if (year != null) {
            text = text.replaceAll("[ .\\-_(\\[\\{]【】（）《》「」『』〕〖〗]*" + year + "[ .\\-_(\\[\\{]【】（）《》「」『』〕〖〗]*", " ");
        }

        String[] cleanPatterns = {
                "(?i)\\b(1080p|2160p|4k|720p|480p|blu[- ]?ray|bluray|bdrip|web[- ]?dl|webrip|remux|x264|x265|h\\.?264|h\\.?265|hevc|avc|vc[- ]?1|aac|dts[- ]?hd|dts|lpcm|truehd|hdr|dv)\\b",
                "(?i)\\b[sS]\\d{1,2}[ ._-]*[-\u2013~][ ._-]*[sS]?\\d{1,2}\\b",
                "(?i)\\bseason\\s*\\d{1,2}\\s*[-\u2013~]\\s*(?:season\\s*)?\\d{1,2}\\b",
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
                "(?i)\\b(disc|disk|cd|dvd)\\s*[-_ ]?\\d{1,2}\\b",
                "(?i)\\b(disc|disk|cd|dvd)\\b",
                "(?i)\\b(bdrom|bdmv)\\b",
                "(?i)\\b(vol|volume)\\s*[-_ .]?\\d{1,2}\\b",
                "(?i)\\b(special\\s*features?|featurettes?)\\b",
                "(?i)\\b\\d{1,3}(st|nd|rd|th)\\s+anniversary(\\s+edition)?\\b",
                "(?i)\\b(anniversary|edition)\\b",
                "(\\u5F71\\u9662|\\u5267\\u573A\\u7248|\\u96FB\\u5F71\\u7248|\\u7535\\u5F71\\u7248|\\u5B8C\\u5168\\u7248|\\u603B\\u96C6\\u7BC7|\\u7279\\u5225\\u7BC7)"
        };
        for (String pattern : cleanPatterns) {
            text = text.replaceAll(pattern, " ");
        }
        text = text.replaceAll("[._]+", " ")
                .replaceAll("\\[[^\\]]*\\]", " ")
                .replaceAll("\\([^)]*\\)", " ")
                .replaceAll("[\\u3010\\u3011\\uFF08\\uFF09\\u300A\\u300B\\u300C\\u300D\\u300E\\u300F\\u3014\\u3015\\u3016\\u3017]", " ")
                .replaceAll("\\s+", " ")
                .trim();

        String foreignTitle = null;
        String chineseTitle = null;
        StringBuilder latin = new StringBuilder();
        for (String token : text.split(" ")) {
            if (token.isEmpty()) {
                continue;
            }
            if (HAN.matcher(token).find()) {
                if (chineseTitle == null) {
                    chineseTitle = "";
                }
                chineseTitle = chineseTitle + (chineseTitle.isEmpty() ? "" : " ") + token;
            } else if (Character.isLetter(token.charAt(0)) || Character.isAlphabetic(token.charAt(0))) {
                latin.append(latin.length() == 0 ? "" : " ").append(token);
            }
        }
        if (latin.length() > 0) {
            foreignTitle = latin.toString().trim();
        }
        if (chineseTitle == null && text.isEmpty()) {
            chineseTitle = "";
        }
        return new SearchMetadata(
                chineseTitle == null || chineseTitle.isEmpty() ? null : chineseTitle,
                foreignTitle,
                text.isEmpty() ? null : text,
                year);
    }

    public static String chooseReleaseYear(String text) {
        if (text == null || text.isEmpty()) {
            return null;
        }
        int latestYear = 0;
        int latestIndex = -1;
        Matcher m = YEAR_PATTERN.matcher(text);
        while (m.find()) {
            int value = Integer.parseInt(m.group());
            if (value >= 1900 && value <= 2100 && value > latestYear) {
                latestYear = value;
                latestIndex = m.start();
            }
        }
        if (latestIndex >= 0) {
            // Prefer the last plausible year (release year usually sits at the tail).
            return String.valueOf(latestYear);
        }
        return null;
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
