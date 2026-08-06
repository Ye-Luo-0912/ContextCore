namespace ContextCore.Tests;

/// <summary>
/// 可手动推进时间的 TimeProvider，供测试精确控制「窗口内 / 超窗」判定。
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _current;

    public ManualTimeProvider(DateTimeOffset? initial = null)
    {
        _current = initial ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _current;

    /// <summary>将当前时间前进指定时长。</summary>
    public void Advance(TimeSpan delta) => _current = _current.Add(delta);
}
