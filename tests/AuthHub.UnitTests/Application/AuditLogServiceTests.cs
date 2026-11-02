using AuthHub.Application.Interfaces;
using AuthHub.Application.Services;
using AuthHub.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AuthHub.UnitTests.Application;

/// <summary>审计日志的登录活动聚合测试。</summary>
public class AuditLogServiceTests
{
    private readonly Mock<IAuditLogRepository> _repository = new();
    private readonly Mock<IClock> _clock = new();
    private readonly AuditLogService _service;

    public AuditLogServiceTests()
    {
        _service = new AuditLogService(
            _repository.Object,
            new Mock<ICurrentUser>().Object,
            _clock.Object,
            NullLogger<AuditLogService>.Instance);
    }

    [Fact]
    public async Task Login_activities_should_fill_thirty_utc_days_and_sum_counts()
    {
        var now = new DateTimeOffset(2026, 11, 2, 15, 30, 0, TimeSpan.Zero);
        var startDate = new DateOnly(2026, 10, 4);
        _clock.SetupGet(x => x.UtcNow).Returns(now);
        DateTimeOffset? queriedFrom = null;
        DateTimeOffset? queriedTo = null;
        _repository
            .Setup(x => x.GetDailyLoginCountsAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>, DateTimeOffset, DateTimeOffset, CancellationToken>(
                (_, from, to, _) =>
                {
                    queriedFrom = from;
                    queriedTo = to;
                })
            .ReturnsAsync(new (string UserId, DateOnly Date, int Count)[]
            {
                ("user-1", startDate, 2),
                ("user-1", new DateOnly(2026, 11, 2), 1)
            });

        var result = await _service.GetLoginActivitiesAsync(new[] { "user-1", "user-2" });

        result.Keys.Should().BeEquivalentTo(new[] { "user-1", "user-2" });
        result["user-1"].Total.Should().Be(3);
        result["user-1"].Days.Should().HaveCount(30);
        result["user-1"].Days[0].Date.Should().Be(startDate);
        result["user-1"].Days[^1].Date.Should().Be(new DateOnly(2026, 11, 2));
        result["user-1"].Days[0].Count.Should().Be(2);
        result["user-1"].Days[^1].Count.Should().Be(1);
        result["user-1"].Days.Where(x => x.Count == 0).Should().HaveCount(28);
        result["user-2"].Total.Should().Be(0);
        result["user-2"].Days.Should().OnlyContain(x => x.Count == 0);
        queriedFrom.Should().Be(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
        queriedTo.Should().Be(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Login_activities_without_users_should_skip_database_query()
    {
        var result = await _service.GetLoginActivitiesAsync(Array.Empty<string>());

        result.Should().BeEmpty();
        _repository.Verify(
            x => x.GetDailyLoginCountsAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
