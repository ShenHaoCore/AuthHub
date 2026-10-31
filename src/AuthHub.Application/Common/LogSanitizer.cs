using System.Text;

namespace AuthHub.Application.Common;

/// <summary>
/// 日志行净化：把可能含换行/控制字符的外部输入压成单行安全文本，阻断日志伪造
/// （攻击者把 \r\n 塞进 URL / 邮箱 / 错误消息，在控制台与文件日志里伪造出以"INFO:"开头的假行）。
/// 结构化字段里的多行值对排障没有增益，被替换成可见转义反而更易读。
/// </summary>
public static class LogSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // 换行类替换成可见转义（多行进日志既伪造行又难读）；其余 C0/C1 控制字符没有可读形态，直接丢弃。
        // char.IsControl 覆盖 0x00-0x1F 与 0x7F-0x9F；正常文本（含中文）原样通过。
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (!char.IsControl(ch))
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
