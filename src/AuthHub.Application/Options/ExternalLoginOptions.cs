namespace AuthHub.Application.Options;

/// <summary>
/// 第三方登录（GitHub / Google）的应用层策略，绑定 <c>AuthHub:Authentication</c> 节，
/// 与 Api 层的提供商凭据配置（Enabled/ClientId/ClientSecret）同节、各自取所需字段。
/// </summary>
public sealed class ExternalLoginOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "AuthHub:Authentication";

    /// <summary>
    /// 允许使用第三方登录的邮箱白名单（对 GitHub / Google 同时生效）。
    ///
    /// <para>
    /// 语义：
    /// <list type="bullet">
    /// <item>空集合（默认）= 不限制，任何拿到「提供商已验证邮箱」的外部账号都可走登录/建号流程；</item>
    /// <item>非空 = 外部账号的已验证邮箱（忽略大小写）必须命中名单，否则一律拒绝。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 白名单在账号查找<em>之前</em>生效，已绑定账号同样受限 —— 否则「先在放开期完成绑定、
    /// 收紧名单后照登不误」就是一条绕过路径。名单开启却拿不到可信邮箱时按未命中处理（fail-closed）。
    /// 典型用途是开发环境只放自己的测试账号；生产保持空数组即可，不要用它做长期访问控制。
    /// </para>
    /// </summary>
    public string[] AllowedEmails { get; set; } = [];
}
