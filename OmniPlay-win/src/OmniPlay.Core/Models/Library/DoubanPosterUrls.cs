namespace OmniPlay.Core.Models.Library;

/// <summary>
/// 豆瓣海报 URL 的可用性判断与归一化，对齐 mac 版 DoubanMetadata 中的同名逻辑。
/// </summary>
public static class DoubanPosterUrls
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = value.Trim();
        if (result.StartsWith("//", StringComparison.Ordinal))
        {
            result = "https:" + result;
        }

        result = result.Replace("/view/photo/large/public/", "/view/photo/s_ratio_poster/public/", StringComparison.Ordinal);
        result = result.Replace("/view/photo/photo/public/", "/view/photo/s_ratio_poster/public/", StringComparison.Ordinal);

        var questionIndex = result.IndexOf('?');
        if (questionIndex >= 0)
        {
            result = result[..questionIndex];
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var lowercased = value.ToLowerInvariant();
        if (!lowercased.StartsWith("http://", StringComparison.Ordinal) && !lowercased.StartsWith("https://", StringComparison.Ordinal))
        {
            return false;
        }

        if (lowercased.Contains("qnmob", StringComparison.Ordinal))
        {
            return false;
        }

        if (lowercased.Contains("/q/60/", StringComparison.Ordinal)
            || lowercased.Contains("/w/300/", StringComparison.Ordinal)
            || lowercased.Contains("/h/300/", StringComparison.Ordinal))
        {
            return false;
        }

        return lowercased.Contains("doubanio.com/view/photo/s_ratio_poster/public/", StringComparison.Ordinal)
            || lowercased.Contains("doubanio.com/view/photo/raw/public/", StringComparison.Ordinal)
            || lowercased.Contains("doubanio.com/view/photo/l/public/", StringComparison.Ordinal);
    }

    public static bool IsUnusableDouban(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lowercased = value.ToLowerInvariant();
        if (lowercased.Contains("qnmob", StringComparison.Ordinal))
        {
            return true;
        }

        return lowercased.Contains("/q/60/", StringComparison.Ordinal)
            || lowercased.Contains("/w/300/", StringComparison.Ordinal)
            || lowercased.Contains("/h/300/", StringComparison.Ordinal)
            || lowercased.Contains("imageview2/1", StringComparison.Ordinal);
    }
}
