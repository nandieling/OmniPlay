package com.omniplay.tv.ui;

import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.drawable.Drawable;

/**
 * 局域网设备行左边的图形：WebDAV 画地球，SMB 画机箱。
 * mac 版用 SF Symbols（globe.asia.australia.fill / server.rack），TV 上没有符号字体，
 * 就照同样的轮廓用 Canvas 画，线宽跟主题里其它描边一个量级。
 */
public final class KindIcons {
    private KindIcons() {
    }

    public static Drawable of(String kind, int sizePx, int color, float strokeWidthPx) {
        return new Glyph("smb".equals(kind), sizePx, color, strokeWidthPx);
    }

    private static final class Glyph extends Drawable {
        private final boolean server;
        private final int size;
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);

        Glyph(boolean server, int size, int color, float stroke) {
            this.server = server;
            this.size = size;
            paint.setColor(color);
            paint.setStrokeWidth(stroke);
            paint.setStrokeCap(Paint.Cap.ROUND);
            paint.setStyle(server ? Paint.Style.FILL_AND_STROKE : Paint.Style.STROKE);
        }

        @Override
        public void draw(Canvas canvas) {
            Rect bounds = getBounds();
            int box = Math.min(bounds.width() > 0 ? bounds.width() : size,
                    bounds.height() > 0 ? bounds.height() : size);
            float inset = box * 0.09f;
            if (server) {
                paint.setStyle(Paint.Style.FILL);
                float bar = (bounds.height() - inset * 2f - box * 0.09f * 2f) / 3f;
                float step = bar + box * 0.09f;
                float radius = bar * 0.3f;
                float dot = box * 0.07f;
                for (int i = 0; i < 3; i++) {
                    float top = bounds.top + inset + i * step;
                    canvas.drawRoundRect(bounds.left + inset, top, bounds.right - inset,
                            top + bar, radius, radius, paint);
                    canvas.drawCircle(bounds.left + inset + bar * 0.7f, top + bar / 2f, dot, paint);
                }
                return;
            }
            float cx = bounds.centerX();
            float cy = bounds.centerY();
            float radius = box / 2f - inset;
            canvas.drawCircle(cx, cy, radius, paint);
            canvas.drawLine(cx - radius, cy, cx + radius, cy, paint);
            canvas.drawLine(cx, cy - radius, cx, cy + radius, paint);
            Path meridian = new Path();
            meridian.addOval(cx - radius * 0.5f, cy - radius, cx + radius * 0.5f, cy + radius,
                    Path.Direction.CW);
            canvas.drawPath(meridian, paint);
        }

        @Override
        public int getIntrinsicWidth() {
            return size;
        }

        @Override
        public int getIntrinsicHeight() {
            return size;
        }

        @Override
        public void setAlpha(int alpha) {
            paint.setAlpha(alpha);
        }

        @Override
        public void setColorFilter(ColorFilter colorFilter) {
            paint.setColorFilter(colorFilter);
        }

        @Override
        public int getOpacity() {
            return PixelFormat.TRANSLUCENT;
        }
    }
}
