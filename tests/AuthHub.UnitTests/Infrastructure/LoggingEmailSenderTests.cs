using AuthHub.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// 邮件兜底通道的按环境脱敏：正文里是一次性重置/确认链接，
/// 受保护环境（非 Development）不得把正文落进日志 —— 即使运维忘了配邮件 provider。
/// </summary>
public class LoggingEmailSenderTests
{
    private const string Body = "重置链接：https://auth.example.com/reset-password?email=a@b.c&token=SECRET";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task SendAsync_in_protected_environments_should_not_log_body_or_token(string environmentName)
    {
        var formatted = await SendAndCapture(environmentName, Body);

        formatted.Should().NotContain("SECRET").And.NotContain("reset-password").And.NotContain(Body);
        formatted.Should().Contain("user@example.com").And.Contain("正文长度");
    }

    [Fact]
    public async Task SendAsync_in_development_should_log_full_body_for_self_testing()
    {
        var formatted = await SendAndCapture("Development", Body);

        formatted.Should().Contain(Body);
    }

    /// <summary>发一封邮件并返回日志渲染后的全文，供断言检索。</summary>
    private static async Task<string> SendAndCapture(string environmentName, string body)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var rendered = new List<string>();
        var logger = new Mock<ILogger<LoggingEmailSender>>();
        logger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(), It.IsAny<EventId>(),
                // It.IsAnyType 的 Moq 限制：回调不能落地成具体委托 —— 在谓词里捕获渲染后的日志行
                It.Is<It.IsAnyType>((state, _) => CaptureState(state, rendered)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Verifiable();

        var sender = new LoggingEmailSender(logger.Object, environment.Object);
        await sender.SendAsync("user@example.com", "【AuthHub】重置您的密码", body);

        return string.Join("|", rendered);
    }

    /// <summary>结构化日志 state 的 ToString() 即模板渲染后的整行。</summary>
    private static bool CaptureState(object? state, List<string> rendered)
    {
        rendered.Add(state?.ToString() ?? string.Empty);
        return true;
    }
}
