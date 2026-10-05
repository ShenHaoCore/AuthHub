using System.Globalization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace AuthHub.Api.TagHelpers;

/// <summary>
/// 后台视图自定义 TagHelper 的公共基类。
///
/// 这一族存在的理由只有一个：**把视图里逐字重复的 HTML 骨架收成一份**。
/// 改造前 7 个后台视图里散着 68 段一模一样的 <c>&lt;svg class="ah-icon" …&gt;</c>
/// （stroke-width / stroke-linecap / aria-hidden 每次都要照抄），
/// 9 个弹窗各自抄一遍 head（标题 + 关闭按钮）、foot（取消 + 提交）与 <c>ReturnUrl</c> 隐藏域。
/// 抄错一处不会报错，只会让某一个图标少个属性、某一个弹窗的按钮点不动。
///
/// 关于"为什么用 TagHelper 而不是 partial 视图"：
///   - partial 无法承载子内容，弹窗的 head / body / foot 夹着任意表单内容，必须能透传；
///   - TagHelper 在**编译期**校验属性名与类型，写错属性名直接编译失败，
///     而 partial 写错模型属性要到运行期才炸；
///   - 复用现成的 <c>TagHelperOutput</c> 管道，不必自己拼字符串再担心编码。
///
/// 子类各自覆写 <see cref="TagHelper.Process"/> / <see cref="TagHelper.ProcessAsync"/> ——
/// 这就是这族类存在的形态：一个基类给出共享的绘制原语，四个子类用不同的方式消费它。
/// </summary>
public abstract class AuthHubTagHelperBase : TagHelper
{
    /// <summary>图标的描边宽度，全站统一。</summary>
    private const string StrokeWidth = "2";

    /// <summary>
    /// 画一个装饰性线稿图标（24×24 视口，跟随 <c>currentColor</c>）。
    ///
    /// 所有图标都是纯装饰：含义一律由所在按钮的 <c>aria-label</c> 或可见文字承担，
    /// 因此统一带 <c>aria-hidden="true"</c>。个别图标原先靠外层
    /// <c>&lt;span aria-hidden&gt;</c> 兜着，这里补上内层标记后只是冗余，不会重复朗读 ——
    /// 但如果哪天外层被去掉，图标不会再变成"无名图形"被读屏念出来。
    /// </summary>
    protected static IHtmlContent Icon(string? pathData, int size)
    {
        var svg = new TagBuilder("svg");
        svg.AddCssClass("ah-icon");
        svg.Attributes["width"] = size.ToString(CultureInfo.InvariantCulture);
        svg.Attributes["height"] = size.ToString(CultureInfo.InvariantCulture);
        svg.Attributes["viewBox"] = "0 0 24 24";
        svg.Attributes["fill"] = "none";
        svg.Attributes["stroke"] = "currentColor";
        svg.Attributes["stroke-width"] = StrokeWidth;
        svg.Attributes["stroke-linecap"] = "round";
        svg.Attributes["stroke-linejoin"] = "round";
        svg.Attributes["aria-hidden"] = "true";

        var path = new TagBuilder("path");
        path.Attributes["d"] = pathData ?? string.Empty;

        // 自闭合渲染，保持与手写 SVG 一致的输出（TagBuilder 渲染成 " />"）
        svg.InnerHtml.AppendHtml(path.RenderSelfClosingTag());

        return svg;
    }

    /// <summary>
    /// 画一个 <c>&lt;button&gt;</c>（属性按传入顺序输出，便于与既有标记逐字对齐）。
    ///
    /// 说明：<c>TagBuilder</c> 只能输出 <c>name="value"</c>，写不出 HTML 里那种无值的
    /// <c>data-dialog-close</c>。这里统一输出 <c>data-dialog-close=""</c> ——
    /// 两者在 DOM 里是同一个东西（HTML 解析器把无值属性规范化成空串），
    /// 前端用的也是 <c>querySelectorAll('[data-dialog-close]')</c> 这种按存在性匹配的选择器。
    /// </summary>
    protected static TagBuilder Button(
        string type,
        string cssClass,
        string text,
        params (string Name, string Value)[] attributes)
    {
        var button = new TagBuilder("button");
        button.Attributes["type"] = type;
        button.AddCssClass(cssClass);

        foreach (var (name, value) in attributes)
        {
            button.Attributes[name] = value;
        }

        button.InnerHtml.Append(text);
        return button;
    }
}
