using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;

namespace AuthHub.Api.Extensions;

/// <summary>
/// 本服务同时存在两类调用方（浏览器走 Identity 会话 Cookie、下游应用与机器客户端走 Bearer 令牌），
/// 显式注册两个方案常量，控制器按需指定，避免依赖全局默认值产生歧义。
/// </summary>
public static class AuthHubSchemes
{
    /// <summary>
    /// Identity 应用会话 Cookie。刻意写成字面量而不是引用 <see cref="IdentityConstants.ApplicationScheme"/>：
    /// 后者是 <c>static readonly</c> 字段而非 <c>const</c>，无法用于
    /// <c>[Authorize(AuthenticationSchemes = ...)]</c> 这类必须编译期常量的场合；
    /// 与 Identity 内部取值的一致性由静态构造函数校验兜底。
    /// </summary>
    public const string Cookie = "Identity.Application";

    /// <summary>OpenIddict 颁发的 Bearer 访问令牌（本地校验）。</summary>
    public const string Bearer = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;

    static AuthHubSchemes()
    {
        if (!string.Equals(Cookie, IdentityConstants.ApplicationScheme, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AuthHubSchemes.Cookie（{Cookie}）与 IdentityConstants.ApplicationScheme" +
                $"（{IdentityConstants.ApplicationScheme}）不一致，请同步修正。");
        }
    }
}
