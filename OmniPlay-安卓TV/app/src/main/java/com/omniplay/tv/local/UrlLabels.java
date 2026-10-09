package com.omniplay.tv.local;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;

/**
 * 给人看的地址：把百分号转义还原成文件夹本来的名字。
 * <p>
 * WebDAV 的 href 必须是编码过的，请求照原样发，只有显示的时候解一层；{@code +} 不解，
 * 路径里的加号就是加号，不能像表单那样当成空格。
 */
public final class UrlLabels {

    private UrlLabels() {
    }

    public static String displayPath(String value) {
        if (value == null || value.isEmpty()) {
            return value == null ? "" : value;
        }
        int query = value.indexOf('?');
        String head = query < 0 ? value : value.substring(0, query);
        if (head.indexOf('%') < 0) {
            return value;
        }
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        StringBuilder result = new StringBuilder();
        int index = 0;
        while (index < head.length()) {
            char c = head.charAt(index);
            if (c == '%' && index + 2 < head.length()
                    && isHex(head.charAt(index + 1)) && isHex(head.charAt(index + 2))) {
                bytes.write((hex(head.charAt(index + 1)) << 4) + hex(head.charAt(index + 2)));
                index += 3;
                continue;
            }
            flush(bytes, result);
            result.append(c);
            index++;
        }
        flush(bytes, result);
        return query < 0 ? result.toString() : result + value.substring(query);
    }

    private static void flush(ByteArrayOutputStream bytes, StringBuilder target) {
        if (bytes.size() == 0) {
            return;
        }
        target.append(new String(bytes.toByteArray(), StandardCharsets.UTF_8));
        bytes.reset();
    }

    private static boolean isHex(char c) {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    private static int hex(char c) {
        return c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
    }
}
