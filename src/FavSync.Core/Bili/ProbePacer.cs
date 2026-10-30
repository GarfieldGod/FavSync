namespace FavSync.Core.Bili;

/// <summary>
/// 探测限速器：AIMD 节流 + 撞墙熔断。
///
/// 为什么需要它（实测）：
///   1.6 QPS 连打约 100 条就撞 B 站风控，此后 playurl 恒回 code=0 但 dash/durl 全空；
///   风控窗口大约 4.5 分钟（序列上能看到第 131 条自己恢复），而旧的固定退避只有 1.5s/3s，
///   等于没退 —— 一整段 50 条每条烧 5.4s、全部写成 unknown，占掉整次作业 77% 的时间且零有效产出。
///
/// 现在的做法：
///   · 撞墙立即熔断，冷却 60s → 180s → 300s（连续撞才升级，恢复后逐步回落）；
///   · 冷却结束后的**第一条**就是探针（canary）：成功才继续，失败继续升档冷却；
///   · 基础间隔用 AIMD：连续成功慢慢提速（下限 320ms），撞一次就大幅放慢（上限 1.6s）。
///     宁可慢一点匀速跑完，也不要猛冲到墙前面用 9 倍单价买一堆假 unknown。
/// </summary>
public sealed class ProbePacer
{
    private const int MinDelayMs = 320;
    private const int MaxDelayMs = 1600;
    private const int StartDelayMs = 400;
    private const int SpeedUpStepMs = 25;    // 每连续成功一条，提速一点点
    private const int SlowDownStepMs = 240;  // 撞一次墙就明显放慢
    private const int CleanToRelax = 8;      // 冷却后连续成功 8 条，才把冷却档位降一级

    private static readonly int[] CooldownSec = { 60, 180, 300 };

    private readonly object _gate = new();
    private int _delayMs = StartDelayMs;
    private int _cooldownStep;               // 冷却档位索引
    private int _cleanStreak;                // 上次冷却后连续成功条数
    private DateTime _blockedUntil = DateTime.MinValue;

    /// <summary>当前基础间隔（毫秒），UI 用它解释"为什么现在慢"。</summary>
    public int DelayMs { get { lock (_gate) return _delayMs; } }

    /// <summary>累计触发的熔断次数。</summary>
    public int CooldownCount { get; private set; }

    /// <summary>还要等多少秒才解除熔断（0 = 没在冷却）。</summary>
    public int CooldownLeftSec
    {
        get
        {
            lock (_gate)
            {
                var left = _blockedUntil - DateTime.UtcNow;
                return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalSeconds) : 0;
            }
        }
    }

    public bool IsCoolingDown => CooldownLeftSec > 0;

    /// <summary>一条探出了结论（exact/lower/gone，或确定的网络失败结论）→ 慢慢提速。</summary>
    public void ReportClean()
    {
        lock (_gate)
        {
            _delayMs = Math.Max(MinDelayMs, _delayMs - SpeedUpStepMs);
            if (_blockedUntil <= DateTime.UtcNow)
            {
                if (++_cleanStreak >= CleanToRelax && _cooldownStep > 0) _cooldownStep--;
                _cleanStreak = 0;
            }
        }
    }

    /// <summary>撞风控（playurl 静默降级）→ 放慢 + 熔断。</summary>
    /// <returns>本次冷却秒数。</returns>
    public int ReportThrottled()
    {
        lock (_gate)
        {
            _delayMs = Math.Min(MaxDelayMs, _delayMs + SlowDownStepMs);
            _cleanStreak = 0;
            var sec = CooldownSec[Math.Min(_cooldownStep, CooldownSec.Length - 1)];
            _cooldownStep = Math.Min(_cooldownStep + 1, CooldownSec.Length - 1);
            _blockedUntil = DateTime.UtcNow.AddSeconds(sec);
            CooldownCount++;
            return sec;
        }
    }

    /// <summary>
    /// 等下一个探测名额：先等完熔断冷却，再等基础间隔（带抖动）。
    /// <paramref name="onCooldown"/> 每秒回调一次剩余秒数，作业靠它把"在冷却"写进进度消息，
    /// 否则用户只会看到进度条不动。
    /// </summary>
    public async Task WaitTurnAsync(CancellationToken ct, Action<int>? onCooldown = null)
    {
        while (true)
        {
            int left;
            lock (_gate) left = (int)Math.Ceiling((_blockedUntil - DateTime.UtcNow).TotalSeconds);
            if (left <= 0) break;
            onCooldown?.Invoke(left);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        int delay;
        lock (_gate) delay = _delayMs;
        await Task.Delay(delay + Random.Shared.Next(80), ct).ConfigureAwait(false);
    }
}
