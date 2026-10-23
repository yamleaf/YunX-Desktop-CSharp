using System.Diagnostics;

namespace YunX.Core.Download;

/// <summary>
/// 全局限速器（令牌桶）。
///
/// <para>比"每块下载完 sleep 一会儿"好在：<b>不占着连接睡觉</b>。
/// 睡眠式限速会让 8 条连接同时进入 sleep，造成周期性集体停顿（也就是"速度忽快忽慢"），
/// 令牌桶只让**需要令牌的那一次读取**等待，其余时间连接都在正常传数据。</para>
///
/// <para>实现要点：桶容量 = limit（允许 1 秒突发）；令牌按实际经过时间连续补充；
/// 等待时把锁释放（<c>await Task.Delay</c> 在锁外），不会阻塞其他下载线程。</para>
/// </summary>
public sealed class SpeedLimiter
{
    private readonly object _gate = new();
    private double _tokens;
    private long _lastRefillTicks;
    private long _limit;   // 字节/秒；0 = 不限速

    public SpeedLimiter(long bytesPerSecond = 0)
    {
        _limit = Math.Max(0, bytesPerSecond);
        _tokens = _limit;
        _lastRefillTicks = Stopwatch.GetTimestamp();
    }

    public long Limit
    {
        get { lock (_gate) return _limit; }
        set
        {
            lock (_gate)
            {
                _limit = Math.Max(0, value);
                _tokens = Math.Min(_tokens, _limit > 0 ? _limit : 0);
                _lastRefillTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// 申请 <paramref name="bytes"/> 个令牌，不足则等待。
    /// </summary>
    public async Task AcquireAsync(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;

        while (true)
        {
            double waitSeconds;

            lock (_gate)
            {
                if (_limit <= 0) return;   // 不限速

                RefillLocked();

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                // 还差多少，按当前速率折算等待时间（夹在 1ms..200ms，避免长睡）
                double need = bytes - _tokens;
                waitSeconds = Math48.Clamp(need / _limit, 0.001, 0.2);
            }

            // 注意：等待在锁外进行，其他线程仍可取令牌
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false);
        }
    }

    private void RefillLocked()
    {
        if (_limit <= 0) return;
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _lastRefillTicks) / (double)Stopwatch.Frequency;
        if (elapsed <= 0) return;

        _tokens = Math.Min(_limit, _tokens + elapsed * _limit);
        _lastRefillTicks = now;
    }
}
