using AuthHub.Domain.Interfaces;

namespace AuthHub.Infrastructure.Time;

/// <summary>基于系统时钟的 <see cref="IClock"/> 实现。</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
