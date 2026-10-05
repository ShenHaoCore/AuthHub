namespace AuthHub.Domain.Interfaces;

/// <summary>
/// 时间抽象。让 Application 层的时间逻辑（令牌过期、审计时间戳）可被单元测试控制。
/// 实现位于 Infrastructure 层（<c>SystemClock</c>）。
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
