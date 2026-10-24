package com.omniplay.tv.ui;

import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.PorterDuff;
import android.graphics.PorterDuffXfermode;
import android.graphics.Rect;
import android.graphics.RectF;
import android.graphics.drawable.Drawable;

/**
 * mac 版工具栏用的是 SF Symbols，TV 上没有这套字体，就照着那几个符号自己画。对应关系与 mac
 * 版 PosterWallView / MovieCardView 里写的 systemName 一一对齐：
 *
 * <ul>
 *   <li>{@code magnifyingglass} → {@link Kind#SEARCH}</li>
 *   <li>{@code line.3.horizontal.decrease.circle} → {@link Kind#SORT_MENU}</li>
 *   <li>{@code arrow.up} / {@code arrow.down} → {@link Kind#ARROW_UP} / {@link Kind#ARROW_DOWN}</li>
 *   <li>{@code arrow.triangle.2.circlepath} → {@link Kind#SYNC}</li>
 *   <li>{@code folder.badge.plus} → {@link Kind#FOLDER_PLUS}</li>
 *   <li>{@code gearshape} → {@link Kind#GEAR}</li>
 *   <li>{@code checkmark.circle.fill} / {@code circle} → 海报下的观看状态</li>
 *   <li>{@code star} / {@code star.fill} → 目录浏览器里「挂载这个目录」的星标</li>
 *   <li>{@code text.magnifyingglass} → {@link Kind#MATCH}（详情页「手动匹配」）</li>
 *   <li>{@code pencil} → {@link Kind#PENCIL}（详情页「自定义编辑」）</li>
 * </ul>
 *
 * 全部用 Canvas 矢量画：聚焦或换主题时换支笔重画一张就行，不引入位图，也不占内存缓存。
 * 所有形状按 24 单位网格设计（Apple 的符号画布就是 24pt），缩到实际尺寸时线宽跟着换算。
 */
public final class Icons {
    public enum Kind {
        SEARCH, SYNC, FOLDER_PLUS, GEAR, SORT_MENU, CHEVRONS, ARROW_UP, ARROW_DOWN, SPINNER,
        WATCH_NONE, WATCH_PART, WATCH_DONE, STAR, STAR_FILL, MATCH, PENCIL
    }

    private Icons() {
    }

    public static Drawable of(Kind kind, int sizePx, int color, float strokePx) {
        return new Glyph(kind, sizePx, color, strokePx);
    }

    private static final class Glyph extends Drawable {
        private final Kind kind;
        private final int size;
        private final float stroke;
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint knockout = new Paint(Paint.ANTI_ALIAS_FLAG);

        Glyph(Kind kind, int size, int color, float strokePx) {
            this.kind = kind;
            this.size = size;
            this.stroke = Math.max(1f, strokePx);
            paint.setColor(color);
            paint.setStrokeWidth(stroke);
            paint.setStrokeCap(Paint.Cap.ROUND);
            paint.setStrokeJoin(Paint.Join.ROUND);
            paint.setStyle(Paint.Style.STROKE);
            knockout.setStrokeWidth(stroke);
            knockout.setStrokeCap(Paint.Cap.ROUND);
            knockout.setStrokeJoin(Paint.Join.ROUND);
            knockout.setStyle(Paint.Style.STROKE);
            knockout.setXfermode(new PorterDuffXfermode(PorterDuff.Mode.CLEAR));
        }

        @Override
        public void draw(Canvas canvas) {
            Rect bounds = getBounds();
            float box = Math.min(bounds.width() > 0 ? bounds.width() : size,
                    bounds.height() > 0 ? bounds.height() : size);
            float left = bounds.left + (bounds.width() - box) / 2f;
            float top = bounds.top + (bounds.height() - box) / 2f;
            float cx = left + box / 2f;
            float cy = top + box / 2f;
            float unit = box / 24f;
            paint.setStrokeWidth(stroke);
            knockout.setStrokeWidth(stroke);

            switch (kind) {
                case SEARCH: {
                    // magnifyingglass：左上镜筒 + 右下柄
                    canvas.drawCircle(left + unit * 10.3f, top + unit * 10.3f, unit * 6.3f, paint);
                    canvas.drawLine(left + unit * 15.0f, top + unit * 15.0f,
                            left + unit * 20.4f, top + unit * 20.4f, paint);
                    break;
                }
                case SORT_MENU: {
                    // line.3.horizontal.decrease.circle：圆里三条左对齐、逐条变短
                    canvas.drawCircle(cx, cy, box * 0.40f, paint);
                    float[] right = {16.7f, 13.9f, 11.2f};
                    for (int line = 0; line < 3; line++) {
                        float y = top + unit * (8.4f + line * 3.6f);
                        canvas.drawLine(left + unit * 7.3f, y, left + unit * right[line], y, paint);
                    }
                    break;
                }
                case ARROW_UP:
                case ARROW_DOWN: {
                    // arrow.up / arrow.down：通高箭杆 + 45° 张开箭头
                    boolean up = kind == Kind.ARROW_UP;
                    float tail = up ? top + unit * 20.2f : top + unit * 3.8f;
                    float head = up ? top + unit * 4.2f : top + unit * 19.8f;
                    float barb = up ? top + unit * 10.4f : top + unit * 13.6f;
                    canvas.drawLine(cx, tail, cx, head, paint);
                    canvas.drawLine(left + unit * 5.8f, barb, cx, head, paint);
                    canvas.drawLine(cx, head, left + unit * 18.2f, barb, paint);
                    break;
                }
                case CHEVRONS: {
                    canvas.drawLine(left + unit * 6.6f, top + unit * 9.6f, cx, top + unit * 5.0f, paint);
                    canvas.drawLine(cx, top + unit * 5.0f, left + unit * 17.4f, top + unit * 9.6f, paint);
                    canvas.drawLine(left + unit * 6.6f, top + unit * 14.4f, cx, top + unit * 19.0f, paint);
                    canvas.drawLine(cx, top + unit * 19.0f, left + unit * 17.4f, top + unit * 14.4f, paint);
                    break;
                }
                case SYNC: {
                    // arrow.triangle.2.circlepath：两段弧，各自顺着切线方向带箭头
                    float radius = box * 0.32f;
                    RectF ring = new RectF(cx - radius, cy - radius, cx + radius, cy + radius);
                    float[][] spans = {{-70f, 150f}, {110f, 150f}};
                    for (float[] span : spans) {
                        canvas.drawArc(ring, span[0], span[1], false, paint);
                        float end = (float) Math.toRadians(span[0] + span[1]);
                        float x = cx + radius * (float) Math.cos(end);
                        float y = cy + radius * (float) Math.sin(end);
                        // 沿角度递增方向走，切线方向就是终角 + 90°
                        drawArrowHead(canvas, x, y, span[0] + span[1] + 90f, unit * 3.6f);
                    }
                    break;
                }
                case FOLDER_PLUS: {
                    // folder.badge.plus：提手在左、加号徽标压在右上角上沿。徽标先用 CLEAR 挖出
                    // 一圈空白再画，否则加号会跟文件夹的轮廓线糊成一团（1.24 那版就糊在底边上）。
                    Path folder = new Path();
                    folder.moveTo(left + unit * 2.4f, top + unit * 19.8f);
                    folder.lineTo(left + unit * 2.4f, top + unit * 7.4f);
                    folder.lineTo(left + unit * 8.4f, top + unit * 7.4f);
                    folder.lineTo(left + unit * 10.2f, top + unit * 9.6f);
                    folder.lineTo(left + unit * 21.6f, top + unit * 9.6f);
                    folder.lineTo(left + unit * 21.6f, top + unit * 19.8f);
                    folder.close();
                    float badgeX = left + unit * 18.4f;
                    float badgeY = top + unit * 7.6f;
                    int layer = canvas.saveLayer(left, top, left + box, top + box, null);
                    canvas.drawPath(folder, paint);
                    Paint erase = new Paint(paint);
                    erase.setStyle(Paint.Style.FILL);
                    erase.setXfermode(new PorterDuffXfermode(PorterDuff.Mode.CLEAR));
                    canvas.drawCircle(badgeX, badgeY, unit * 4.4f, erase);
                    Paint badge = new Paint(paint);
                    badge.setStyle(Paint.Style.FILL);
                    canvas.drawCircle(badgeX, badgeY, unit * 4.0f, badge);
                    canvas.drawLine(badgeX - unit * 2.1f, badgeY, badgeX + unit * 2.1f, badgeY, knockout);
                    canvas.drawLine(badgeX, badgeY - unit * 2.1f, badgeX, badgeY + unit * 2.1f, knockout);
                    canvas.restoreToCount(layer);
                    break;
                }
                case GEAR: {
                    // gearshape：实心齿圈 + 八颗齿 + 中心孔。齿要画成短粗的胶囊，
                    // 画成从圆心发散的细线就只有「太阳」的意思，不像齿轮。
                    Paint body = new Paint(paint);
                    body.setStrokeWidth(stroke * 1.5f);
                    Paint tooth = new Paint(paint);
                    tooth.setStrokeWidth(stroke * 2.1f);
                    canvas.save();
                    for (int index = 0; index < 8; index++) {
                        canvas.rotate(45f, cx, cy);
                        canvas.drawLine(cx, cy - unit * 5.2f, cx, cy - unit * 8.6f, tooth);
                    }
                    canvas.restore();
                    canvas.drawCircle(cx, cy, unit * 5.6f, body);
                    Paint hole = new Paint(paint);
                    hole.setStrokeWidth(stroke * 0.9f);
                    canvas.drawCircle(cx, cy, unit * 2.3f, hole);
                    break;
                }
                case SPINNER: {
                    RectF ring = new RectF(cx - box * 0.32f, cy - box * 0.32f,
                            cx + box * 0.32f, cy + box * 0.32f);
                    canvas.drawArc(ring, -90f, 285f, false, paint);
                    break;
                }
                case MATCH: {
                    // text.magnifyingglass：三行文字从左上排下来、长短递降，放大镜压在右下。
                    // 先在独立图层上把圆周一圈抠掉再画镜筒（与 WATCH_DONE 同一手法），
                    // 笔画互不粘连，22dp 下也分得清是「翻资料找片名」。
                    int layer = canvas.saveLayer(left, top, left + box, top + box, null);
                    float[] lineRight = {17.6f, 13.0f, 11.2f};
                    for (int line = 0; line < lineRight.length; line++) {
                        float y = top + unit * (5.0f + line * 4.2f);
                        canvas.drawLine(left + unit * 3.4f, y,
                                left + unit * lineRight[line], y, paint);
                    }
                    float lensX = left + unit * 16.3f;
                    float lensY = top + unit * 15.9f;
                    float lensR = unit * 4.6f;
                    Paint halo = new Paint(knockout);
                    halo.setStyle(Paint.Style.FILL);
                    canvas.drawCircle(lensX, lensY, lensR + unit * 1.9f, halo);
                    canvas.drawCircle(lensX, lensY, lensR, paint);
                    canvas.drawLine(lensX + lensR * 0.72f, lensY + lensR * 0.72f,
                            left + unit * 21.4f, top + unit * 21.4f, paint);
                    canvas.restoreToCount(layer);
                    break;
                }
                case PENCIL: {
                    // pencil：斜杆 + 笔尖 + 一道箍线，端点落在 24 网格边内。
                    Path pencil = new Path();
                    pencil.moveTo(left + unit * 4.2f, top + unit * 19.8f);
                    pencil.lineTo(left + unit * 6.0f, top + unit * 14.6f);
                    pencil.lineTo(left + unit * 16.4f, top + unit * 4.2f);
                    pencil.lineTo(left + unit * 19.8f, top + unit * 7.6f);
                    pencil.lineTo(left + unit * 9.4f, top + unit * 18.0f);
                    pencil.close();
                    canvas.drawPath(pencil, paint);
                    canvas.drawLine(left + unit * 13.9f, top + unit * 6.7f,
                            left + unit * 17.3f, top + unit * 10.1f, paint);
                    break;
                }
                case STAR:
                case STAR_FILL: {
                    // 五角星按 24 单位网格取五个顶点（外接圆半径 8.6，顶角朝上），
                    // 内圈半径按正五角星的比例 0.382 倍算，跟 SF Symbols 的 star 一致。
                    Path star = new Path();
                    float outer = box * 0.358f;
                    float inner = outer * 0.44f;
                    for (int point = 0; point < 10; point++) {
                        double angle = Math.PI / 2 + point * Math.PI / 5;
                        float radius = (point & 1) == 0 ? outer : inner;
                        float x = cx + (float) Math.cos(angle) * radius;
                        float y = cy - (float) Math.sin(angle) * radius;
                        if (point == 0) {
                            star.moveTo(x, y);
                        } else {
                            star.lineTo(x, y);
                        }
                    }
                    star.close();
                    if (kind == Kind.STAR_FILL) {
                        Paint filled = new Paint(paint);
                        filled.setStyle(Paint.Style.FILL);
                        canvas.drawPath(star, filled);
                    } else {
                        canvas.drawPath(star, paint);
                    }
                    break;
                }
                case WATCH_NONE: {
                    canvas.drawCircle(cx, cy, box * 0.36f, paint);
                    break;
                }
                case WATCH_PART: {
                    RectF ring = new RectF(cx - box * 0.36f, cy - box * 0.36f,
                            cx + box * 0.36f, cy + box * 0.36f);
                    Paint filled = new Paint(paint);
                    filled.setStyle(Paint.Style.FILL);
                    canvas.drawArc(ring, -90f, 180f, true, filled);
                    canvas.drawCircle(cx, cy, box * 0.36f, paint);
                    break;
                }
                case WATCH_DONE: {
                    int layer = canvas.saveLayer(left, top, left + box, top + box, null);
                    Paint filled = new Paint(paint);
                    filled.setStyle(Paint.Style.FILL);
                    canvas.drawCircle(cx, cy, box * 0.36f, filled);
                    Path check = new Path();
                    check.moveTo(cx - box * 0.16f, cy + box * 0.01f);
                    check.lineTo(cx - box * 0.04f, cy + box * 0.13f);
                    check.lineTo(cx + box * 0.18f, cy - box * 0.11f);
                    canvas.drawPath(check, knockout);
                    canvas.restoreToCount(layer);
                    break;
                }
                default:
                    break;
            }
        }

        /** 箭头尖：以 (x, y) 为顶点、沿 angle 方向回头张开的一对小翅膀。 */
        private void drawArrowHead(Canvas canvas, float x, float y, float angleDegrees, float length) {
            double radians = Math.toRadians(angleDegrees);
            double spread = Math.toRadians(150);
            canvas.drawLine(x, y,
                    x + (float) (Math.cos(radians + spread) * length),
                    y + (float) (Math.sin(radians + spread) * length), paint);
            canvas.drawLine(x, y,
                    x + (float) (Math.cos(radians - spread) * length),
                    y + (float) (Math.sin(radians - spread) * length), paint);
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
