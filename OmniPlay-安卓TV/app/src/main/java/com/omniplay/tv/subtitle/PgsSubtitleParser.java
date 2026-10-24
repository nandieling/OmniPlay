package com.omniplay.tv.subtitle;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public final class PgsSubtitleParser {
    private static final int SEGMENT_PALETTE = 0x14;
    private static final int SEGMENT_OBJECT = 0x15;
    private static final int SEGMENT_PRESENTATION = 0x16;
    private static final int SEGMENT_END = 0x80;
    private static final double PTS_TIMESCALE = 90000d;
    private static final double FALLBACK_CUE_SECONDS = 8d;

    private PgsSubtitleParser() {
    }

    public static List<PgsSubtitleCue> parse(InputStream input) throws IOException {
        Parser parser = new Parser(input);
        return parser.parse();
    }

    private static final class Parser {
        private final InputStream input;
        private final ArrayList<PgsSubtitleCue> cues = new ArrayList<>();
        private final Map<Integer, ObjectBuilder> objectBuilders = new HashMap<>();
        private final Map<Integer, ObjectData> objects = new HashMap<>();
        private int[] palette = new int[256];
        private boolean hasPalette;
        private Presentation presentation;
        private PgsSubtitleCue openCue;

        Parser(InputStream input) {
            this.input = input;
        }

        List<PgsSubtitleCue> parse() throws IOException {
            while (readSegment()) {
            }
            closeOpenCue(openCue == null ? 0 : openCue.startSeconds + FALLBACK_CUE_SECONDS);
            return cues;
        }

        private boolean readSegment() throws IOException {
            int markerA = input.read();
            if (markerA < 0) {
                return false;
            }

            int markerB = input.read();
            if (markerB < 0) {
                return false;
            }

            // 同步字是 0x50 0x4A，也就是 'P' 'J'（经常被误写成 'PG'）。
            if (markerA != 'P' || markerB != 'J') {
                return true;
            }

            try {
                // 段头共 10 字节：'PJ' + 5 字节 PTS + 段类型 + 16 位长度。
                // PTS 沿用 MPEG 时间戳写法：'1000' + pts_ext(3) + '1' | pts[32..18] + '1' | pts[17..3] + '1'。
                readUnsignedByte();
                int highStart = readUnsignedByte();
                int highEnd = readUnsignedByte();
                int lowStart = readUnsignedByte();
                int lowEnd = readUnsignedByte();
                long ptsEighths = (long) ((((highStart << 8) | highEnd) >>> 1) << 15)
                        | ((((lowStart << 8) | lowEnd) >>> 1) & 0x7FFF);
                int type = readUnsignedByte();
                int length = readUnsignedShort();
                byte[] payload = readFully(length);
                handleSegment(type, ptsEighths * 8 / PTS_TIMESCALE, payload);
                return true;
            } catch (EOFException ignored) {
                return false;
            }
        }

        private void handleSegment(int type, double ptsSeconds, byte[] payload) {
            if (type == SEGMENT_PALETTE) {
                parsePalette(payload);
            } else if (type == SEGMENT_OBJECT) {
                parseObject(payload);
            } else if (type == SEGMENT_PRESENTATION) {
                closeOpenCue(ptsSeconds);
                presentation = parsePresentation(payload, ptsSeconds);
                if (presentation.objects.isEmpty()) {
                    presentation = null;
                }
            } else if (type == SEGMENT_END) {
                emitCue();
                presentation = null;
            }
        }

        private void parsePalette(byte[] payload) {
            if (payload.length < 7) {
                return;
            }

            int[] nextPalette = palette.clone();
            for (int offset = 2; offset + 4 < payload.length; offset += 5) {
                int id = payload[offset] & 0xff;
                int y = payload[offset + 1] & 0xff;
                int cr = payload[offset + 2] & 0xff;
                int cb = payload[offset + 3] & 0xff;
                int alpha = payload[offset + 4] & 0xff;
                int r = clamp((int) Math.round(y + 1.402d * (cr - 128)));
                int g = clamp((int) Math.round(y - 0.34414d * (cb - 128) - 0.71414d * (cr - 128)));
                int b = clamp((int) Math.round(y + 1.772d * (cb - 128)));
                nextPalette[id] = alpha == 0 ? 0 : (alpha << 24) | (r << 16) | (g << 8) | b;
            }

            palette = nextPalette;
            hasPalette = true;
        }

        private void parseObject(byte[] payload) {
            if (payload.length < 4) {
                return;
            }

            int objectId = readUnsignedShortFrom(payload, 0);
            int sequence = payload[3] & 0xff;
            int offset = 4;
            ObjectBuilder builder = objectBuilders.get(objectId);
            if ((sequence & 0x80) != 0) {
                if (payload.length < 11) {
                    return;
                }
                int objectDataLength = readUnsignedInt24(payload, 4);
                int width = readUnsignedShortFrom(payload, 7);
                int height = readUnsignedShortFrom(payload, 9);
                builder = new ObjectBuilder(objectId, width, height, Math.max(0, objectDataLength - 4));
                objectBuilders.put(objectId, builder);
                offset = 11;
            }

            if (builder == null) {
                return;
            }

            if (offset < payload.length) {
                builder.append(payload, offset, payload.length - offset);
            }

            if ((sequence & 0x40) != 0 || builder.isComplete()) {
                if (builder.hasData()) {
                    objects.put(objectId, builder.build());
                }
                objectBuilders.remove(objectId);
            }
        }

        private Presentation parsePresentation(byte[] payload, double ptsSeconds) {
            if (payload.length < 11) {
                return Presentation.empty(ptsSeconds);
            }

            int planeWidth = readUnsignedShortFrom(payload, 0);
            int planeHeight = readUnsignedShortFrom(payload, 2);
            int objectCount = payload[10] & 0xff;
            ArrayList<PresentationObject> presentationObjects = new ArrayList<>(objectCount);
            int offset = 11;
            for (int index = 0; index < objectCount && offset + 7 < payload.length; index++) {
                int objectId = readUnsignedShortFrom(payload, offset);
                offset += 2;
                offset++;
                int flags = payload[offset++] & 0xff;
                int x = readUnsignedShortFrom(payload, offset);
                offset += 2;
                int y = readUnsignedShortFrom(payload, offset);
                offset += 2;
                if ((flags & 0x80) != 0 && offset + 7 < payload.length) {
                    offset += 8;
                }
                presentationObjects.add(new PresentationObject(objectId, x, y));
            }

            return new Presentation(ptsSeconds, planeWidth, planeHeight, presentationObjects);
        }

        private void emitCue() {
            if (presentation == null || !hasPalette) {
                return;
            }

            ArrayList<PgsSubtitleCue.Image> images = new ArrayList<>(presentation.objects.size());
            int[] cuePalette = palette.clone();
            for (PresentationObject object : presentation.objects) {
                ObjectData data = objects.get(object.objectId);
                if (data == null || data.width <= 0 || data.height <= 0 || data.rleData.length == 0) {
                    continue;
                }
                images.add(new PgsSubtitleCue.Image(
                        object.objectId,
                        object.x,
                        object.y,
                        data.width,
                        data.height,
                        cuePalette,
                        data.rleData));
            }

            if (images.isEmpty()) {
                return;
            }

            PgsSubtitleCue cue = new PgsSubtitleCue(
                    presentation.startSeconds,
                    Double.POSITIVE_INFINITY,
                    presentation.planeWidth,
                    presentation.planeHeight,
                    images);
            cues.add(cue);
            openCue = cue;
        }

        private void closeOpenCue(double endSeconds) {
            if (openCue == null) {
                return;
            }

            openCue.endSeconds = Math.max(openCue.startSeconds, endSeconds);
            openCue = null;
        }

        private int readUnsignedByte() throws IOException {
            int value = input.read();
            if (value < 0) {
                throw new EOFException();
            }
            return value;
        }

        private int readUnsignedShort() throws IOException {
            int high = readUnsignedByte();
            int low = readUnsignedByte();
            return (high << 8) | low;
        }

        private byte[] readFully(int length) throws IOException {
            byte[] data = new byte[Math.max(0, length)];
            int offset = 0;
            while (offset < data.length) {
                int count = input.read(data, offset, data.length - offset);
                if (count < 0) {
                    throw new EOFException();
                }
                offset += count;
            }
            return data;
        }
    }

    private static int readUnsignedShortFrom(byte[] data, int offset) {
        return ((data[offset] & 0xff) << 8) | (data[offset + 1] & 0xff);
    }

    private static int readUnsignedInt24(byte[] data, int offset) {
        return ((data[offset] & 0xff) << 16) | ((data[offset + 1] & 0xff) << 8) | (data[offset + 2] & 0xff);
    }

    private static int clamp(int value) {
        return Math.max(0, Math.min(255, value));
    }

    private static final class Presentation {
        final double startSeconds;
        final int planeWidth;
        final int planeHeight;
        final List<PresentationObject> objects;

        Presentation(double startSeconds, int planeWidth, int planeHeight, List<PresentationObject> objects) {
            this.startSeconds = Math.max(0, startSeconds);
            this.planeWidth = Math.max(1, planeWidth);
            this.planeHeight = Math.max(1, planeHeight);
            this.objects = objects;
        }

        static Presentation empty(double startSeconds) {
            return new Presentation(startSeconds, 1, 1, new ArrayList<>());
        }
    }

    private static final class PresentationObject {
        final int objectId;
        final int x;
        final int y;

        PresentationObject(int objectId, int x, int y) {
            this.objectId = objectId;
            this.x = Math.max(0, x);
            this.y = Math.max(0, y);
        }
    }

    private static final class ObjectBuilder {
        final int objectId;
        final int width;
        final int height;
        final int expectedLength;
        final ByteArrayOutputStream data;

        ObjectBuilder(int objectId, int width, int height, int expectedLength) {
            this.objectId = objectId;
            this.width = Math.max(0, width);
            this.height = Math.max(0, height);
            this.expectedLength = Math.max(0, expectedLength);
            this.data = new ByteArrayOutputStream(Math.max(32, Math.min(this.expectedLength, 256 * 1024)));
        }

        void append(byte[] source, int offset, int length) {
            int remaining = expectedLength <= 0 ? length : expectedLength - data.size();
            int count = Math.max(0, Math.min(length, remaining));
            if (count > 0) {
                data.write(source, offset, count);
            }
        }

        boolean hasData() {
            return data.size() > 0;
        }

        boolean isComplete() {
            return expectedLength > 0 && data.size() >= expectedLength;
        }

        ObjectData build() {
            byte[] bytes = data.toByteArray();
            if (expectedLength > 0 && bytes.length > expectedLength) {
                bytes = Arrays.copyOf(bytes, expectedLength);
            }
            return new ObjectData(objectId, width, height, bytes);
        }
    }

    private static final class ObjectData {
        final int objectId;
        final int width;
        final int height;
        final byte[] rleData;

        ObjectData(int objectId, int width, int height, byte[] rleData) {
            this.objectId = objectId;
            this.width = width;
            this.height = height;
            this.rleData = rleData;
        }
    }
}
