using System.Text.Json;
using AuthHub.Application.Options;

namespace AuthHub.Infrastructure.Security;

/// <summary>CIMD 客户端元数据（<c>client_id</c> 指向的那份 JSON）—— 解析与校验。</summary>
/// <remarks>
/// - 手写解析而非反序列化到 DTO：每个字段都是不可信输入，校验规则要逐条显式表达并与错误信息一一对应；
///   "缺 client_name"和"client_name 是空串"必须同样被拒。
/// - 元数据里的 scope 不可信（元数据由客户端自托管即攻击者控制），白名单 <see cref="CimdOptions.AllowedScopes"/> 是唯一边界。
/// </remarks>
public sealed record CimdClientMetadata
{
    public required string ClientId { get; init; }

    public required string ClientName { get; init; }

    /// <summary>回调地址，已按 native/loopback 规则归一化（去掉显式端口）。</summary>
    public required string[] RedirectUris { get; init; }

    /// <summary><c>web</c> 或 <c>native</c>。</summary>
    public required string ApplicationType { get; init; }

    public required string[] GrantTypes { get; init; }

    /// <summary>与白名单取交集之后的 scope。</summary>
    public required string[] Scopes { get; init; }

    /// <summary>
    /// 解析并校验元数据文档；任一项不满足即失败，错误信息会进日志与审计，被拒方据此修正自己的元数据。
    /// </summary>
    public static bool TryParse(
        string document,
        string expectedClientId,
        CimdOptions options,
        out CimdClientMetadata? metadata,
        out string? error)
    {
        metadata = null;
        error = null;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(document);
        }
        catch (JsonException)
        {
            error = "元数据不是合法的 JSON。";
            return false;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "元数据必须是 JSON 对象。";
                return false;
            }

            // ① client_id 必须等于请求用的 URL —— 否则一份文档可以替别的客户端"背书"冒名
            var declaredId = ReadString(root, "client_id");
            if (string.IsNullOrEmpty(declaredId))
            {
                error = "元数据缺少 client_id。";
                return false;
            }

            if (!string.Equals(declaredId, expectedClientId, StringComparison.Ordinal))
            {
                error = "元数据里的 client_id 与请求使用的不一致。";
                return false;
            }

            // ② client_name 必填：同意页要展示"是哪个应用在要权限"，缺了等于没有知情同意
            var clientName = ReadString(root, "client_name");
            if (string.IsNullOrWhiteSpace(clientName))
            {
                error = "元数据缺少 client_name。";
                return false;
            }

            // ③ application_type：缺省按 web 处理（更严格的一侧）
            var applicationType = ReadString(root, "application_type")?.Trim().ToLowerInvariant() ?? "web";
            if (applicationType is not ("web" or "native"))
            {
                error = "application_type 只支持 web 或 native。";
                return false;
            }

            // ④ 认证方式：CIMD 没有登记密钥的环节，只可能是 public（none）；声明其它方式无法验证，只能拒
            var authMethod = ReadString(root, "token_endpoint_auth_method")?.Trim() ?? "none";
            if (!string.Equals(authMethod, "none", StringComparison.OrdinalIgnoreCase))
            {
                error = "token_endpoint_auth_method 只能是 none（CIMD 客户端按 public 处理）。";
                return false;
            }

            // ⑤ 授权类型：只要授权码（+ 刷新），不要客户端凭证
            var declaredGrants = ReadStringArray(root, "grant_types");
            var grantTypes = declaredGrants is { Length: > 0 }
                ? declaredGrants
                : ["authorization_code", "refresh_token"];

            foreach (var grant in grantTypes)
            {
                if (grant is not ("authorization_code" or "refresh_token"))
                {
                    error = $"不支持的授权类型：{grant}。自动注册客户端只允许 authorization_code 与 refresh_token。";
                    return false;
                }
            }

            if (!grantTypes.Contains("authorization_code", StringComparer.Ordinal))
            {
                error = "自动注册客户端必须支持 authorization_code。";
                return false;
            }

            // ⑥ 回调地址：必填、非空、逐条规范化
            if (!TryReadRedirectUris(root, applicationType, out var redirectUris, out error))
            {
                return false;
            }

            // ⑦ scope：与白名单取交集（不能原样相信客户端自报的权限范围）
            if (!TryResolveScopes(root, options, out var scopes, out error))
            {
                return false;
            }

            metadata = new CimdClientMetadata
            {
                ClientId = declaredId,
                ClientName = clientName.Trim(),
                RedirectUris = redirectUris,
                ApplicationType = applicationType,
                GrantTypes = grantTypes,
                Scopes = scopes
            };

            return true;
        }
    }

    /// <summary>
    /// 校验并规范化回调地址（对应 MCP 规范对 <c>application_type</c> 的要求）：web 不允许回环地址；
    /// native 允许回环且按 RFC 8252 §7.3 去掉显式端口 —— OpenIddict 的 relaxed 比较只在
    /// "请求带非默认端口、库里存默认端口"时生效，必须入库前去端口。
    /// </summary>
    private static bool TryReadRedirectUris(
        JsonElement root,
        string applicationType,
        out string[] redirectUris,
        out string? error)
    {
        redirectUris = [];
        error = null;

        var declared = ReadStringArray(root, "redirect_uris");
        if (declared is not { Length: > 0 })
        {
            error = "元数据缺少 redirect_uris。";
            return false;
        }

        var normalized = new List<string>(declared.Length);

        foreach (var candidate in declared)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                error = $"redirect_uri 不是绝对地址：{candidate}。";
                return false;
            }

            // 片段不会随请求发给服务端，允许它等于"校验的字符串"和"实际用的地址"不是同一个
            if (!string.IsNullOrEmpty(uri.Fragment))
            {
                error = $"redirect_uri 不允许包含片段：{candidate}。";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                error = $"redirect_uri 不允许包含凭据：{candidate}。";
                return false;
            }

            var isHttp = uri.Scheme is "http" or "https";

            if (isHttp)
            {
                if (CimdAddressPolicy.IsLoopbackHost(uri))
                {
                    if (applicationType != "native")
                    {
                        error = "web 类型的客户端不允许使用回环地址作为 redirect_uri。";
                        return false;
                    }

                    // 去掉端口：让 OpenIddict 的 native relaxed 比较生效
                    normalized.Add(new UriBuilder(uri) { Port = -1 }.Uri.AbsoluteUri);
                    continue;
                }

                // 非回环的 http 一律不允许：授权码会明文经过网络
                if (uri.Scheme == "http")
                {
                    error = $"redirect_uri 必须使用 https（回环地址除外）：{candidate}。";
                    return false;
                }
            }
            else if (applicationType != "native")
            {
                // 自定义 scheme（cursor://、vscode:// 等）只对 native 应用有意义
                error = $"web 类型的客户端不支持自定义 scheme：{candidate}。";
                return false;
            }

            normalized.Add(uri.AbsoluteUri);
        }

        redirectUris = normalized.Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool TryResolveScopes(
        JsonElement root,
        CimdOptions options,
        out string[] scopes,
        out string? error)
    {
        scopes = [];
        error = null;

        var allowed = options.AllowedScopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (allowed.Length == 0)
        {
            error = "服务端未配置可自动授予的 scope（AuthHub:Cimd:AllowedScopes）。";
            return false;
        }

        // scope 在元数据里是空格分隔的字符串（与授权请求一致），不是数组
        var declared = ReadString(root, "scope")
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        if (declared.Length == 0)
        {
            scopes = allowed;
            return true;
        }

        var effective = declared.Intersect(allowed, StringComparer.Ordinal).ToArray();

        if (effective.Length == 0)
        {
            error = $"元数据申请的 scope（{string.Join(' ', declared)}）均不在允许范围内。";
            return false;
        }

        scopes = effective;
        return true;
    }

    // ------------------------------------------------------------------ JSON 读取辅助

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[]? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<string>(value.GetArrayLength());

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                items.Add(text.Trim());
            }
        }

        return items.ToArray();
    }
}
