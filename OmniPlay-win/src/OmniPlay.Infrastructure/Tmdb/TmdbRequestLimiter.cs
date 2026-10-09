using System.Diagnostics;

namespace OmniPlay.Infrastructure.Tmdb;

/// <summary>
/// TMDB 请求节流器（对齐 mac 版 <c>TMDBRateLimiter</c>）：按每秒请求数摊平请求间隔，
/// 避免扫描/刮削时突发打满配额换来 429。
/// </summary>
public sealed class TmdbRequestLimiter
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly double minIntervalMs;
    private long nextAllowedTimestamp;

    public TmdbRequestLimiter(double requestsPerSecond)
    {
        var safeRps = Math.Max(0.5, requestsPerSecond);
        minIntervalMs = 1000.0 / safeRps;
    }

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var next = Interlocked.Read(ref nextAllowedTimestamp);
            if (now < next)
            {
                var delayMs = (next - now) * 1000.0 / Stopwatch.Frequency;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, delayMs)), cancellationToken).ConfigureAwait(false);
                now = Stopwatch.GetTimestamp();
            }

            Interlocked.Exchange(ref nextAllowedTimestamp, now + (long)(minIntervalMs * Stopwatch.Frequency / 1000.0));
        }
        finally
        {
            gate.Release();
        }
    }
}
