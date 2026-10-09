using System.Net;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace AuthHub.IntegrationTests;

public class DevSmsInboxTests
{
    [Fact]
    public async Task Sms_inbox_should_return_404_outside_development()
    {
        // 默认 Testing 环境：不应暴露调试页
        using var factory = new AuthHubWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/dev/sms-inbox");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sms_inbox_should_render_in_development()
    {
        using var factory = new AuthHubWebApplicationFactory(environment: "Development");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/dev/sms-inbox");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("短信收件箱");
        html.Should().Contain("暂无短信");
    }
}
