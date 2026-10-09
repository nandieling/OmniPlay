package com.omniplay.tv.local;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

/**
 * 蓝光光盘目录的文件挑选规则，移植自 mac 版 MediaNameParser.selectedBluRayStreamIndices。
 * <p>
 * 一张光盘的 BDMV/STREAM 里通常有几十条 m2ts：正片、花絮、宣传片、分段。整目录入库会让一部片子
 * 在首页变成十几条记录，所以扫描时必须先在每个分组里只留下主片。mac 版先按体积判断，体积拿不到
 * 时退回时长；扫描阶段只有体积可用，所以这里只保留尺寸这条判据。
 */
public final class BluRayStreams {

    /** 一条蓝光分卷：相对路径 + 文件体积，体积未知时为 0。 */
    public static final class Candidate {
        public final String fileName;
        public final String relativePath;
        public final long sizeBytes;

        public Candidate(String fileName, String relativePath, long sizeBytes) {
            this.fileName = fileName;
            this.relativePath = relativePath;
            this.sizeBytes = sizeBytes;
        }
    }

    /** 最大分卷比第二名大出这个比例，就认为它是单独一个正片，和 mac 版一致。 */
    private static final double SINGLE_FEATURE_RATIO = 1.55;
    /** 分卷合辑的兜底比例：不小于最大分卷的这个比例就一起留下。 */
    private static final double CLUSTER_RATIO = 0.72;

    private BluRayStreams() {
    }

    /** 路径是否穿过 BDMV：说明这是蓝光光盘结构里的文件。 */
    public static boolean isDiscPath(String relativePath) {
        return bdmvIndex(relativePath) >= 0;
    }

    /** 只有 BDMV/STREAM 下的分卷才是正片候选；CLIPINF、PLAYLIST 等不进媒体库。 */
    public static boolean isStreamPath(String relativePath) {
        int index = bdmvIndex(relativePath);
        if (index < 0) {
            return false;
        }
        String[] parts = pathParts(relativePath);
        return index + 1 < parts.length && parts[index + 1].equalsIgnoreCase("STREAM");
    }

    /** 分组键：BDMV 上一级的路径，同一张盘的所有分卷都会归到同一个键。 */
    public static String discFolderOf(String relativePath) {
        int index = bdmvIndex(relativePath);
        if (index <= 0) {
            return "";
        }
        String[] parts = pathParts(relativePath);
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < index; i++) {
            if (builder.length() > 0) {
                builder.append('/');
            }
            builder.append(parts[i]);
        }
        return builder.toString();
    }

    /** 只留主片。等价于 mac 版 includeExtras = false。 */
    public static List<Candidate> selectMainFeatures(List<Candidate> streams) {
        return selectStreams(streams, false);
    }

    /**
     * 分卷先按「数字分卷号 → 文件名」升序排好，再按体积挑主片：最大的一条明显大于第二名
     * （1.55 倍）就是单文件正片；否则按 0.72 的门槛把同一部片子的分段一起留下。
     * 体积全都不知道时退回排序后的第一条，至少不会一个条目都不剩。
     */
    public static List<Candidate> selectStreams(List<Candidate> streams, boolean includeExtras) {
        List<Candidate> ordered = new ArrayList<>(streams);
        Collections.sort(ordered, PLAYBACK_ORDER);
        if (ordered.isEmpty()) {
            return new ArrayList<>();
        }
        if (includeExtras) {
            return ordered;
        }
        List<Candidate> bySize = mainFeatures(ordered, SIZE_METRIC);
        if (bySize != null) {
            return bySize;
        }
        return new ArrayList<>(ordered.subList(0, 1));
    }

    private static final ToLong SIZE_METRIC = candidate -> Math.max(0, candidate.sizeBytes);

    /** 按某个度量挑主片；这个度量一个已知值都没有时返回 null，交给下一种判据。 */
    private static List<Candidate> mainFeatures(List<Candidate> ordered, ToLong metric) {
        List<Candidate> known = new ArrayList<>();
        for (Candidate candidate : ordered) {
            if (metric.applyAsLong(candidate) > 0) {
                known.add(candidate);
            }
        }
        if (known.isEmpty()) {
            return null;
        }
        List<Candidate> byMetric = new ArrayList<>(known);
        byMetric.sort((left, right) -> {
            long leftMetric = metric.applyAsLong(left);
            long rightMetric = metric.applyAsLong(right);
            if (leftMetric != rightMetric) {
                return Long.compare(rightMetric, leftMetric);
            }
            return PLAYBACK_ORDER.compare(left, right);
        });
        Candidate largest = byMetric.get(0);
        long largestMetric = metric.applyAsLong(largest);
        if (byMetric.size() == 1) {
            return single(largest);
        }
        long secondMetric = metric.applyAsLong(byMetric.get(1));
        if (secondMetric <= 0
                || (double) largestMetric >= (double) secondMetric * SINGLE_FEATURE_RATIO) {
            return single(largest);
        }
        // 体积接近：这是分卷合辑，整组留下，否则正片会被切掉一段。
        long threshold = Math.round((double) largestMetric * CLUSTER_RATIO);
        List<Candidate> cluster = new ArrayList<>();
        for (Candidate candidate : ordered) {
            if (metric.applyAsLong(candidate) >= threshold) {
                cluster.add(candidate);
            }
        }
        return cluster.isEmpty() ? single(largest) : cluster;
    }

    private static List<Candidate> single(Candidate candidate) {
        List<Candidate> result = new ArrayList<>(1);
        result.add(candidate);
        return result;
    }

    private interface ToLong {
        long applyAsLong(Candidate candidate);
    }

    /** 分卷号（00001.m2ts → 1）优先，非数字的分卷排在后面，再按文件名。 */
    private static final Comparator<Candidate> PLAYBACK_ORDER = (left, right) -> {
        long leftNumber = streamNumber(left.fileName);
        long rightNumber = streamNumber(right.fileName);
        if (leftNumber != rightNumber) {
            return Long.compare(leftNumber, rightNumber);
        }
        return left.fileName.toLowerCase(Locale.ROOT)
                .compareTo(right.fileName.toLowerCase(Locale.ROOT));
    };

    private static long streamNumber(String fileName) {
        String stem = stripExtension(lastSegment(fileName));
        try {
            return Long.parseLong(stem.trim());
        } catch (NumberFormatException ignored) {
            return Long.MAX_VALUE;
        }
    }

    private static int bdmvIndex(String relativePath) {
        if (relativePath == null || relativePath.isEmpty()) {
            return -1;
        }
        String[] parts = pathParts(relativePath);
        for (int index = 0; index < parts.length; index++) {
            if (parts[index].equalsIgnoreCase("BDMV")) {
                return index;
            }
        }
        return -1;
    }

    private static String[] pathParts(String relativePath) {
        return relativePath.replace('\\', '/').split("/");
    }

    private static String lastSegment(String path) {
        String normalized = path.replace('\\', '/');
        int slash = normalized.lastIndexOf('/');
        return slash >= 0 ? normalized.substring(slash + 1) : normalized;
    }

    private static String stripExtension(String name) {
        int dot = name.lastIndexOf('.');
        return dot > 0 ? name.substring(0, dot) : name;
    }
}
