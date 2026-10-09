using System.Collections.Concurrent;

namespace AuthHub.Infrastructure.Notifications;

/// <summary>
/// Development 短信收件箱：进程内保留最近若干条，供 <c>/dev/sms-inbox</c> 查看。
/// </summary>
public sealed class SmsDevInbox
{
    public const int Capacity = 50;

    private readonly ConcurrentQueue<SmsDevMessage> _messages = new();
    private int _count;

    public void Add(string phoneNumber, string message)
    {
        _messages.Enqueue(new SmsDevMessage(phoneNumber, message, DateTimeOffset.UtcNow));
        if (Interlocked.Increment(ref _count) > Capacity)
        {
            if (_messages.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
            }
        }
    }

    public IReadOnlyList<SmsDevMessage> Snapshot()
        => _messages.Reverse().ToArray();
}

public sealed record SmsDevMessage(string PhoneNumber, string Message, DateTimeOffset SentAtUtc);
