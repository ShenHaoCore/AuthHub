using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace AuthHub.Api.TagHelpers;

/// <summary>
/// <c>&lt;ah-icon path="…" size="16" /&gt;</c> —— 装饰性线稿图标。
///
/// 全站 68 处 <c>&lt;svg&gt;</c> 的唯一画法，见 <see cref="AuthHubTagHelperBase.Icon"/>。
/// 用法：<c>&lt;ah-icon path="@AdminNav.Icons.Plus" size="15" /&gt;</c>
/// </summary>
[HtmlTargetElement("ah-icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper : AuthHubTagHelperBase
{
    /// <summary>24×24 视口下的 <c>path</c> 的 <c>d</c> 值，通常取自 <c>AdminNav.Icons</c>。</summary>
    [HtmlAttributeName("path")]
    public string? Path { get; set; }

    /// <summary>边长（像素）。默认 16，与 /admin 里最常用的尺寸一致。</summary>
    [HtmlAttributeName("size")]
    public int Size { get; set; } = 16;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        // 干掉 <ah-icon> 这层壳，只留下 SVG 本身
        output.TagName = null;
        output.Content.SetHtmlContent(Icon(Path, Size));
    }
}

/// <summary>
/// <c>&lt;ah-dialog-body&gt;…&lt;/ah-dialog-body&gt;</c> —— 弹窗的内容区。
///
/// 它只做一件事：把子内容套进 <c>&lt;div class="ah-modal-body"&gt;</c>。
/// 单独留一个标签而不是让 &lt;ah-dialog&gt; 自动包一层，是因为表单
/// （<c>&lt;form asp-page-handler&gt;</c>）必须留在视图里 ——
/// Razor 的 FormTagHelper 负责生成 action 与防伪令牌，自己拼容易静默丢掉防伪令牌。
/// </summary>
[HtmlTargetElement("ah-dialog-body")]
public sealed class DialogBodyTagHelper : AuthHubTagHelperBase
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "ah-modal-body");
    }
}

/// <summary>
/// <c>&lt;ah-dialog-foot dismiss="取消" submit="保存" /&gt;</c> —— 弹窗底部的操作区。
///
/// 两种形态：
///   - 只给 <c>dismiss</c>（如只读弹窗）：按钮靠右，文案就是 dismiss；
///   - 同时给 <c>submit</c>：dismiss 在左、主按钮在右（<c>ah-modal-foot-end</c> 里）。
/// </summary>
[HtmlTargetElement("ah-dialog-foot", TagStructure = TagStructure.WithoutEndTag)]
public sealed class DialogFootTagHelper : AuthHubTagHelperBase
{
    /// <summary>次要按钮（默认「取消」）的文案；它会带上 <c>data-dialog-close</c> 关闭弹窗。</summary>
    [HtmlAttributeName("dismiss")]
    public string Dismiss { get; set; } = "取消";

    /// <summary>主按钮（<c>type="submit"</c>）的文案。留空表示这个弹窗没有提交动作。</summary>
    [HtmlAttributeName("submit")]
    public string? Submit { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;

        var foot = new TagBuilder("div");
        foot.AddCssClass("ah-modal-foot");

        var hasSubmit = !string.IsNullOrWhiteSpace(Submit);
        var dismiss = Button("button", "ah-btn", Dismiss, ("data-dialog-close", string.Empty));

        if (hasSubmit)
        {
            foot.InnerHtml.AppendHtml(dismiss);

            var end = new TagBuilder("div");
            end.AddCssClass("ah-modal-foot-end");
            end.InnerHtml.AppendHtml(Button(
                "submit",
                "ah-btn ah-btn--primary",
                Submit!));

            foot.InnerHtml.AppendHtml(end);
        }
        else
        {
            // 只读弹窗：没有提交动作，唯一的按钮靠右
            var end = new TagBuilder("div");
            end.AddCssClass("ah-modal-foot-end");
            end.InnerHtml.AppendHtml(dismiss);

            foot.InnerHtml.AppendHtml(end);
        }

        output.Content.SetHtmlContent(foot);
    }
}

/// <summary>
/// <c>&lt;ah-dialog id="dlg-create" title="新建用户" auto-open="…" size="lg"&gt;…&lt;/ah-dialog&gt;</c>
/// —— 弹窗外壳 + 标题栏。
///
/// 标题栏（标题 + 关闭按钮）由本类产出，子内容原样透传，因此调用方只需要关心表单字段：
///
/// <code>
/// &lt;ah-dialog id="dlg-create" title="新建用户" auto-open="@(Model.AutoOpenCreate ? "true" : "false")"&gt;
///   &lt;form method="post" asp-page-handler="create"&gt;
///     &lt;input type="hidden" name="ReturnUrl" value="@Model.CurrentUrl" /&gt;
///     &lt;ah-dialog-body&gt; …字段… &lt;/ah-dialog-body&gt;
///     &lt;ah-dialog-foot submit="创建用户" /&gt;
///   &lt;/form&gt;
/// &lt;/ah-dialog&gt;
/// </code>
///
/// <c>auto-open</c> 有两个必须守住的约束（<c>AdminUiTests</c> 盯着）：
///   1. 取值只能是显式的小写字面量 "true" / "false" —— 直接把它绑成 <c>bool</c> 属性，
///      Razor 会渲染成 "True"/"False"，而前端按值判断，契约随即失效；
///   2. 不传时**整条属性都不输出**（只读弹窗就是这样），而不是输出 "false"。
/// 因此这里把它收成字符串按原样透传，由调用方决定写什么。
/// </summary>
[HtmlTargetElement("ah-dialog")]
public sealed class DialogTagHelper : AuthHubTagHelperBase
{
    /// <summary>关闭按钮的无障碍名称。</summary>
    private const string CloseLabel = "关闭";

    /// <summary>弹窗 id。标题栏的 <c>&lt;h2&gt;</c> 取 <c>{id}-title</c>，与 <c>aria-labelledby</c> 对齐。</summary>
    [HtmlAttributeName("id")]
    public string? Id { get; set; }

    /// <summary>标题栏文案。</summary>
    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    /// <summary>自动打开标记。传 "true"/"false"；不传则整条属性不输出。</summary>
    [HtmlAttributeName("auto-open")]
    public string? AutoOpen { get; set; }

    /// <summary>传 "lg" 得到宽弹窗（<c>ah-modal-lg</c>），用于字段多或含权限树的表单。</summary>
    [HtmlAttributeName("size")]
    public string? Size { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var id = Id ?? string.Empty;

        output.TagName = "dialog";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", string.Equals(Size, "lg", StringComparison.OrdinalIgnoreCase)
            ? "ah-modal ah-modal-lg"
            : "ah-modal");
        output.Attributes.SetAttribute("id", id);
        output.Attributes.SetAttribute("aria-labelledby", $"{id}-title");

        if (!string.IsNullOrEmpty(AutoOpen))
        {
            output.Attributes.SetAttribute("data-dialog-autoopen", AutoOpen);
        }

        // 标题栏必须排在子内容（表单）之前 —— PreContent 正好插在开标签与子内容之间
        output.PreContent.SetHtmlContent(Head(id, Title ?? string.Empty));
    }

    private static IHtmlContent Head(string id, string title)
    {
        var head = new TagBuilder("div");
        head.AddCssClass("ah-modal-head");

        var heading = new TagBuilder("h2");
        heading.Attributes["id"] = $"{id}-title";
        heading.InnerHtml.Append(title);
        head.InnerHtml.AppendHtml(heading);

        var close = Button(
            "button",
            "ah-icon-btn ah-modal-close",
            string.Empty,
            ("data-dialog-close", string.Empty),
            ("aria-label", CloseLabel));

        close.InnerHtml.AppendHtml(Icon(ClosePath, 16));
        head.InnerHtml.AppendHtml(close);

        return head;
    }

    /// <summary>
    /// 关闭按钮的叉号。两个子路径各自是一条直线（<c>M18 6 L6 18</c> 与 <c>M6 6 l12 12</c>），
    /// 因此 <c>stroke-linejoin</c> 对它没有任何视觉效果 —— 它原先没写这个属性，
    /// 交给统一的 Icon() 画之后会带上，属于等价改写。
    /// </summary>
    private const string ClosePath = "M18 6L6 18M6 6l12 12";
}
