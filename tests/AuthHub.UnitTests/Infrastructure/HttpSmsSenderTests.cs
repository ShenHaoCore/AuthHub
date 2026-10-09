using System.Net;
using System.Text;
using AuthHub.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AuthHub.UnitTests.Infrastructure;

public class HttpSmsSenderTests
{
    [Fact]
    public async Task Send_should_post_json_with_bearer_and_succeed_on_2xx()
    {
        string? body = null;
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpSmsSender.HttpClientName))
            .Returns(new HttpClient(handler));

        var sender = new HttpSmsSender(
            factory.Object,
            Options.Create(new SmsOptions
            {
                Provider = "http",
                HttpUrl = "https://sms.example.com/send",
                ApiKey = "secret-token"
            }),
            NullLogger<HttpSmsSender>.Instance);

        await sender.SendAsync("+8613800138000", "【AuthHub】验证码 123456，5 分钟内有效。");

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.ToString().Should().Be("https://sms.example.com/send");
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("secret-token");
        body.Should().Contain("phoneNumber");
        body.Should().Contain("8613800138000");
        body.Should().Contain("123456");
    }

    [Fact]
    public async Task Send_should_throw_when_http_url_missing()
    {
        var factory = new Mock<IHttpClientFactory>();
        var sender = new HttpSmsSender(
            factory.Object,
            Options.Create(new SmsOptions { Provider = "http", HttpUrl = "" }),
            NullLogger<HttpSmsSender>.Instance);

        var act = () => sender.SendAsync("13800138000", "hi");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*HttpUrl*");
    }

    [Fact]
    public async Task Send_should_throw_when_gateway_returns_error()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("quota exceeded", Encoding.UTF8, "text/plain")
        }));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpSmsSender.HttpClientName))
            .Returns(new HttpClient(handler));

        var sender = new HttpSmsSender(
            factory.Object,
            Options.Create(new SmsOptions
            {
                Provider = "http",
                HttpUrl = "https://sms.example.com/send"
            }),
            NullLogger<HttpSmsSender>.Instance);

        var act = () => sender.SendAsync("13800138000", "hi");

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*400*");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _responder(request);
    }
}
