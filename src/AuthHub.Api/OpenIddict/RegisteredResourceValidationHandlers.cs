using System.Collections.Immutable;
using AuthHub.Application.Interfaces;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using SR = OpenIddict.Abstractions.OpenIddictResources;

namespace AuthHub.Api.OpenIddict;

/// <summary>
/// 资源白名单校验（RFC 8707 Resource Indicators）—— **库驱动**的运行时实现。
///
/// <para><b>为什么替换内置 handler</b>:OpenIddict 7.7.2 内置的
/// <c>OpenIddictServerHandlers.Exchange.ValidateResources</c>（令牌端点）与
/// <c>OpenIddictServerHandlers.Authentication.ValidateResources</c>（授权端点）对照的是
/// <c>OpenIddictServerOptions.Resources</c> —— 启动时快照的集合,7.7.2 没有运行时增删接口。
/// 这两个 handler 在 AddAuthHubOpenIddict 里被精确移除,换成这里的读库实现:
/// 每个带 <c>resource</c> 参数的请求都对照 RegisteredResources 表中**启用中**的资源,
/// 管理 API 增删/启停后下一个请求立即生效,多实例部署也天然一致。</para>
///
/// <para><b>默认拒绝的红线</b>:方向永远是「查库命中才放行」—— 表为空时,任何 resource
/// 参数都会被 <c>invalid_target</c> 拒绝;这条链路任何修改都必须保持这个失败方向。</para>
///
/// <para>与内置实现的语义对齐:① URI 形态合法性（绝对 URI、无 fragment）仍由内置的
/// <c>ValidateResourceParameter</c> 负责,这里只做白名单成员比对;② 比对方式与旧实现一致 ——
/// 请求原文 vs 登记值（<c>Uri.AbsoluteUri</c> 形态）的序数字符串比对,因此登记入口
/// （种子与管理 API）都必须先经 <c>AuthHubResourceRegistry.NormalizeResource</c> 规范化。</para>
/// </summary>
public static class RegisteredResourceValidationHandlers
{
    /// <summary>
    /// 两个 handler 的描述符。order 与被替换的内置 handler 完全一致
    /// （各自 <c>ValidateAudiences</c> 之后 1000）,确保校验链的时序不变。
    /// </summary>
    public static ImmutableArray<OpenIddictServerHandlerDescriptor> Descriptors { get; } =
    [
        Token.Descriptor,
        Authorization.Descriptor
    ];

    /// <summary>令牌端点（/connect/token,含 client_credentials / authorization_code 兑换等）。</summary>
    public sealed class Token(IRegisteredResourceStore store) : IOpenIddictServerHandler<ValidateTokenRequestContext>
    {
        public static OpenIddictServerHandlerDescriptor Descriptor { get; }
            = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
                .AddFilter<OpenIddictServerHandlerFilters.RequireResourceValidationEnabled>()
                .UseScopedHandler<Token>()
                .SetOrder(OpenIddictServerHandlers.Exchange.ValidateAudiences.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.BuiltIn)
                .Build();

        public async ValueTask HandleAsync(ValidateTokenRequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            // 与内置实现同构:先转 HashSet(序数比较),再与白名单做集合差
            var requested = context.Request.GetResources().ToHashSet(StringComparer.Ordinal);
            if (requested.Count is 0)
            {
                return;
            }

            await ValidateAsync(requested, store, context);
        }
    }

    /// <summary>授权端点（/connect/authorize,MCP 客户端走授权码流程的第一跳）。</summary>
    public sealed class Authorization(IRegisteredResourceStore store) : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
    {
        public static OpenIddictServerHandlerDescriptor Descriptor { get; }
            = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
                .AddFilter<OpenIddictServerHandlerFilters.RequireResourceValidationEnabled>()
                .UseScopedHandler<Authorization>()
                .SetOrder(OpenIddictServerHandlers.Authentication.ValidateScopes.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.BuiltIn)
                .Build();

        public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var requested = context.Request.GetResources().ToHashSet(StringComparer.Ordinal);
            if (requested.Count is 0)
            {
                return;
            }

            await ValidateAsync(requested, store, context);
        }
    }

    private static async ValueTask ValidateAsync(
        HashSet<string> requested,
        IRegisteredResourceStore store,
        BaseValidatingContext context)
    {
        var registered = await store.GetEnabledResourcesAsync();

        // ExceptWith = 内置同款写法:差集非空即拒绝,一个不多放
        var unknown = requested.ToHashSet(StringComparer.Ordinal);
        unknown.ExceptWith(registered);

        if (unknown.Count is 0)
        {
            return;
        }

        context.Reject(
            error: Errors.InvalidTarget,
            description: SR.FormatID2190(Parameters.Resource),
            uri: SR.FormatID8000(SR.ID2190));
    }
}
