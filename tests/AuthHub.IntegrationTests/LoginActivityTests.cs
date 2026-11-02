using AuthHub.Domain.Entities;
using AuthHub.Domain.Enums;
using AuthHub.Infrastructure.Data;
using AuthHub.Infrastructure.Repositories;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AuthHub.IntegrationTests;

/// <summary>登录活动的真实数据库聚合测试。</summary>
[Collection(AuthHubFixture.CollectionName)]
public class LoginActivityTests
{
    private readonly AuthHubFixture _fixture;

    public LoginActivityTests(AuthHubFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Daily_counts_should_include_only_completed_logins_in_range()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthHubDbContext>();
        var now = DateTimeOffset.UtcNow;
        var from = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-29), TimeSpan.Zero);
        var to = from.AddDays(30);
        db.AuditLogs.AddRange(
            Log("user-1", AuditActionType.UserLoginSucceeded, now),
            Log("user-1", AuditActionType.UserExternalLoginSucceeded, now.AddDays(-1)),
            Log("user-1", AuditActionType.TwoFactorRecoveryCodeUsed, now.AddDays(-2)),
            Log("user-1", AuditActionType.UserLoginPasswordSucceeded, now),
            Log("user-1", AuditActionType.UserLoginSucceeded, now, "密码校验通过，等待 MFA"),
            Log("user-1", AuditActionType.UserLoginFailed, now, succeeded: false),
            Log("user-1", AuditActionType.UserLoginSucceeded, from.AddTicks(-1)),
            Log("user-2", AuditActionType.UserLoginSucceeded, now));
        await db.SaveChangesAsync();
        var repository = new AuditLogRepository(db);

        var counts = await repository.GetDailyLoginCountsAsync(new[] { "user-1" }, from, to);

        counts.Sum(x => x.Count).Should().Be(3);
        counts.Should().OnlyContain(x => x.UserId == "user-1");
        counts.Should().Contain(x => x.Date == DateOnly.FromDateTime(now.UtcDateTime) && x.Count == 1);
        counts.Should().Contain(x => x.Date == DateOnly.FromDateTime(now.UtcDateTime.AddDays(-1)) && x.Count == 1);
        counts.Should().Contain(x => x.Date == DateOnly.FromDateTime(now.UtcDateTime.AddDays(-2)) && x.Count == 1);
    }

    private static AuditLog Log(
        string userId,
        string action,
        DateTimeOffset createdAt,
        string? details = null,
        bool succeeded = true) => new()
        {
            UserId = userId,
            Action = action,
            CreatedAt = createdAt,
            Details = details,
            Succeeded = succeeded
        };
}
