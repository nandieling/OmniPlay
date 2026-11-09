package com.omniplay.tv.local;

/**
 * 播放页「现在到底选了哪条字幕」这一小段判定。
 *
 * <p>抽出来只有一个原因：默认字幕那套重试（700ms / 1.8s / 4.2s / 8.5s）反复把用户刚点中的
 * 转档字幕盖掉，换成一条内封轨，而直出模式下内封轨压根不上屏，于是「不报错，也没字幕」。
 * 判定散在 Activity 里没法单独验，这里只放纯逻辑，Activity 负责把状态递进来。
 */
public final class SubtitleSelection {
    private SubtitleSelection() {
    }

    /** 转档 / 媒体源外挂字幕选着没有。 */
    public static boolean sidecarSelected(String selectedSidecarKey) {
        return selectedSidecarKey != null && !selectedSidecarKey.isEmpty();
    }

    /** 覆盖层手里有没有能画的字幕：文本 cue 或位图 cue 至少有一种。 */
    public static boolean cuesInHand(int textCues, int bitmapCues) {
        return textCues > 0 || bitmapCues > 0;
    }

    /**
     * 转档字幕该不该算「已经选上了」：读到手上了算，还在读但没读到过期也算——那时屏幕上挂着
     * 的还是上一条，这时候放手会被默认字幕抢走。
     */
    public static boolean sidecarCounts(String selectedSidecarKey, int textCues, int bitmapCues,
                                        boolean loadStale) {
        if (!sidecarSelected(selectedSidecarKey)) {
            return false;
        }
        return cuesInHand(textCues, bitmapCues) || !loadStale;
    }

    /**
     * 这条片子的字幕归不归 Android 覆盖层画。直出（vo=mediacodec_embed）与 HLS 都归覆盖层：
     * 前一种 mpv 整个不参与画面，内封轨点了也不会上屏；后一种走本地代理，mpv 也画不了。
     */
    public static boolean overlayOwnsSubtitles(boolean directPlayback, boolean isoFile, boolean hlsPlayback) {
        return hlsPlayback || (directPlayback && !isoFile);
    }

    /**
     * 从可用的转档字幕里挑一条：语言（null = 不限）与要不要位图字幕。
     * 文本字幕排在位图前面——位图（PGS）要逐条解码再画，直出模式下更贵。
     */
    public static LocalEngine.Sidecar prefer(java.util.List<LocalEngine.Sidecar> sidecars,
                                             String language,
                                             boolean wantBitmap) {
        if (sidecars == null) {
            return null;
        }
        for (LocalEngine.Sidecar sidecar : sidecars) {
            if (language != null && !language.equals(sidecar.language)) {
                continue;
            }
            if ("sup".equals(sidecar.format) == wantBitmap) {
                return sidecar;
            }
        }
        return null;
    }

    /** 设置里的「关闭字幕」：偏好值写成 no / off 时不要自作主张挂字幕。 */
    public static boolean subtitlePreferenceOff(String languagePreference) {
        return "no".equals(languagePreference) || "off".equals(languagePreference);
    }

    /**
     * 默认字幕那套重试能不能接手当前字幕。
     *
     * @param sidecarOn      转档 / 媒体源外挂字幕选着
     * @param cuesInHand     覆盖层正画着字幕（文本或位图 cue 不为空）
     * @param loadStale      最近一次字幕加载已经拖过 8 秒还没回来
     * @param plainSelection 内封 / 外挂 / 运行时字幕有选择（含正在加载的）
     * @param overlayHandoff 这条片子的字幕由 Android 覆盖层画（直出、HLS）
     * @param pending        内封 / 外挂字幕还在加载中
     */
    public static boolean defaultRetryMayTakeOver(boolean sidecarOn, boolean cuesInHand, boolean loadStale,
                                                  boolean plainSelection, boolean overlayHandoff,
                                                  boolean pending) {
        if (sidecarOn) {
            return !(cuesInHand || !loadStale);
        }
        if (!plainSelection) {
            return true;
        }
        if (overlayHandoff) {
            return !cuesInHand;
        }
        // 兼容模式：内封 / 外挂由 mpv 自己画。还在加载中的话允许重试补一次（调用方还有一道
        // 「没超时就别动」的闸），已经落定的选择就别一遍遍重选了。
        return pending;
    }
}
