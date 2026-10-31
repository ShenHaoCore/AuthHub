using AuthHub.Application.Common;
using FluentAssertions;
using Xunit;

namespace AuthHub.UnitTests.Application;

/// <summary>日志行净化：外部输入里的换行与控制字符必须在进日志前被压平，阻断日志伪造。</summary>
public class LogSanitizerTests
{
    [Theory]
    [InlineData("CRLF 注入\r\nINFO: 伪造的日志行", "CRLF 注入\\r\\nINFO: 伪造的日志行")]
    [InlineData("纯换行\n第二行", "纯换行\\n第二行")]
    [InlineData("制表符\t分隔", "制表符\\t分隔")]
    [InlineData("空字符\u0000与退格\u0008", "空字符与退格")]
    [InlineData("C0/C1 控制符\u0000\u0080\u007F均丢弃", "C0/C1 控制符均丢弃")]
    public void Sanitize_should_flatten_control_characters(string input, string expected)
    {
        LogSanitizer.Sanitize(input).Should().Be(expected);
        // 净化后的文本不再含任何可断行的控制字符 —— 这就是"日志伪造被阻断"的可执行定义
        LogSanitizer.Sanitize(input).Should().NotMatchRegex(@"[\r\n\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]");
    }

    [Fact]
    public void Sanitize_should_keep_normal_text_untouched()
    {
        const string normal = "正常文本：clientId=web-client，URL=https://client.example.com/callback";
        LogSanitizer.Sanitize(normal).Should().Be(normal);
    }

    [Fact]
    public void Sanitize_should_map_null_and_empty_to_empty()
    {
        LogSanitizer.Sanitize(null).Should().BeEmpty();
        LogSanitizer.Sanitize(string.Empty).Should().BeEmpty();
    }
}
