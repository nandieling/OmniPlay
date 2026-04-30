using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OmniPlay.UI.Converters;

public sealed class ImagePathToBitmapConverter : IValueConverter
{
    private const int DefaultDecodePixelWidth = 640;
    private const int MaxCacheEntries = 160;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<ImageCacheKey, Bitmap> Cache = [];
    private static readonly Queue<ImageCacheKey> CacheOrder = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IImage image)
        {
            return image;
        }

        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = ResolveLocalPath(path.Trim());
        if (string.IsNullOrWhiteSpace(normalizedPath) || !File.Exists(normalizedPath))
        {
            return null;
        }

        var fileInfo = new FileInfo(normalizedPath);
        var decodePixelWidth = ResolveDecodePixelWidth(parameter);
        var cacheKey = new ImageCacheKey(
            fileInfo.FullName,
            fileInfo.LastWriteTimeUtc.Ticks,
            fileInfo.Length,
            decodePixelWidth);

        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        using var stream = fileInfo.OpenRead();
        var bitmap = Bitmap.DecodeToWidth(stream, decodePixelWidth, BitmapInterpolationMode.MediumQuality);

        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                bitmap.Dispose();
                return cached;
            }

            Cache[cacheKey] = bitmap;
            CacheOrder.Enqueue(cacheKey);
            TrimCache();
            return bitmap;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static string? ResolveLocalPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : null;
    }

    private static int ResolveDecodePixelWidth(object? parameter)
    {
        return parameter switch
        {
            int width when width > 0 => width,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width > 0 => width,
            _ => DefaultDecodePixelWidth
        };
    }

    private static void TrimCache()
    {
        while (Cache.Count > MaxCacheEntries && CacheOrder.TryDequeue(out var oldestKey))
        {
            if (Cache.Remove(oldestKey, out var bitmap))
            {
                bitmap.Dispose();
            }
        }
    }

    private readonly record struct ImageCacheKey(
        string Path,
        long LastWriteTicks,
        long Length,
        int DecodePixelWidth);
}
