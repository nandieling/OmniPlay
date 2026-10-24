package com.omniplay.tv.subtitle;

import java.io.IOException;

/**
 * 字幕 sidecar 转档要按字节区间读远端文件（MKV 的字幕块与音视频交错在同一字节流里，只能按簇跳读），
 * 所以抽一层「可随机读的字节源」：WebDAV 用 Range GET，SMB 用随机访问文件，本地目录用
 * RandomAccessFile。
 */
public interface SeekableSource {
    long size() throws IOException;

    /**
     * 读取 {@code [offset, offset + length)}。读满返回，越过文件末尾时返回实际读到的部分。
     */
    byte[] readAt(long offset, int length) throws IOException;

    void close() throws IOException;
}
