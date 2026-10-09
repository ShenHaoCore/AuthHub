using System.Globalization;
using AuthHub.Api.Pages;
using AuthHub.Infrastructure.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace AuthHub.Api.Controllers;

/// <summary>
/// Development 专用调试页：查看内存短信收件箱（MFA Phone 等不会真发）。
/// 非 Development 一律 404。
/// </summary>
[Route("dev")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class DevSmsController : Controller
{
    private readonly IHostEnvironment _environment;

    public DevSmsController(IHostEnvironment environment) => _environment = environment;

    [HttpGet("sms-inbox")]
    public IActionResult SmsInbox([FromServices] IServiceProvider services)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var inbox = services.GetService<SmsDevInbox>();
        if (inbox is null)
        {
            return NotFound();
        }

        var items = inbox.Snapshot()
            .Select(m => (
                m.PhoneNumber,
                m.Message,
                m.SentAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
            .ToArray();

        return new ContentResult
        {
            Content = HtmlPages.SmsDevInboxPage(items),
            ContentType = "text/html; charset=utf-8",
            StatusCode = StatusCodes.Status200OK
        };
    }
}
