using AuthHub.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthHub.UnitTests.Infrastructure;

public class DevInboxSmsSenderTests
{
    [Fact]
    public async Task Send_should_capture_message_in_inbox_newest_first()
    {
        var inbox = new SmsDevInbox();
        var sender = new DevInboxSmsSender(inbox, NullLogger<DevInboxSmsSender>.Instance);

        await sender.SendAsync("13800000001", "code-1");
        await sender.SendAsync("13800000002", "code-2");

        var snapshot = inbox.Snapshot();
        snapshot.Should().HaveCount(2);
        snapshot[0].Message.Should().Be("code-2");
        snapshot[0].PhoneNumber.Should().Be("13800000002");
        snapshot[1].Message.Should().Be("code-1");
    }

    [Fact]
    public async Task Inbox_should_drop_oldest_when_over_capacity()
    {
        var inbox = new SmsDevInbox();
        var sender = new DevInboxSmsSender(inbox, NullLogger<DevInboxSmsSender>.Instance);

        for (var i = 0; i < SmsDevInbox.Capacity + 5; i++)
        {
            await sender.SendAsync("13800000000", $"msg-{i}");
        }

        var snapshot = inbox.Snapshot();
        snapshot.Should().HaveCount(SmsDevInbox.Capacity);
        snapshot[0].Message.Should().Be($"msg-{SmsDevInbox.Capacity + 4}");
        snapshot[^1].Message.Should().Be("msg-5");
    }
}
