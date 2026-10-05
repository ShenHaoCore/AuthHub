using AuthHub.Application.DTOs.Clients;
using FluentValidation;

namespace AuthHub.Application.Validators;

/// <summary>创建客户端请求验证。重定向地址必须为绝对 URI；非 localhost 强制 https。</summary>
public sealed class CreateClientRequestValidator : AbstractValidator<CreateClientRequest>
{
    private static readonly string[] AllowedGrantTypes =
    {
        "authorization_code", "refresh_token", "client_credentials", "password", "implicit"
    };

    private static readonly string[] AllowedApplicationTypes = { "web", "native" };
    private static readonly string[] AllowedClientTypes = { "confidential", "public" };

    public CreateClientRequestValidator()
    {
        RuleFor(x => x.ClientId)
            .NotEmpty().WithMessage("ClientId 不能为空。")
            .MinimumLength(3).WithMessage("ClientId 至少 3 位。")
            .MaximumLength(100).WithMessage("ClientId 不能超过 100 位。")
            .Matches("^[a-zA-Z0-9._-]+$").WithMessage("ClientId 只能包含字母、数字以及 . _ - 。");

        RuleFor(x => x.DisplayName)
            .MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.DisplayName));

        RuleFor(x => x.ClientSecret)
            .MinimumLength(8).WithMessage("客户端密钥至少 8 位。")
            .MaximumLength(128)
            .When(x => !string.IsNullOrWhiteSpace(x.ClientSecret));

        RuleFor(x => x.ApplicationType)
            .Must(t => AllowedApplicationTypes.Contains(t!))
            .WithMessage("applicationType 仅支持 web 或 native（纯机器客户端请留空）。")
            .When(x => !string.IsNullOrWhiteSpace(x.ApplicationType));

        RuleFor(x => x.ClientType)
            .Must(t => AllowedClientTypes.Contains(t!))
            .WithMessage("clientType 仅支持 confidential 或 public。")
            .When(x => !string.IsNullOrWhiteSpace(x.ClientType));

        RuleForEach(x => x.GrantTypes)
            .Must(t => AllowedGrantTypes.Contains(t))
            .WithMessage($"grantTypes 仅支持：{string.Join(", ", AllowedGrantTypes)}。")
            .When(x => x.GrantTypes is not null);

        RuleForEach(x => x.Scopes)
            .NotEmpty().WithMessage("scope 不能为空。")
            .MaximumLength(100)
            .When(x => x.Scopes is not null);

        RuleFor(x => x.RedirectUris)
            .Must(uris => UriRules.HasValidUris(uris, out _))
            .WithMessage(UriRules.Message)
            .When(x => x.RedirectUris is not null && x.RedirectUris.Count > 0);

        RuleFor(x => x.PostLogoutRedirectUris)
            .Must(uris => UriRules.HasValidUris(uris, out _))
            .WithMessage(UriRules.Message)
            .When(x => x.PostLogoutRedirectUris is not null && x.PostLogoutRedirectUris.Count > 0);

        // public 客户端（SPA / 移动端）不允许携带密钥，安全性由 PKCE 保证
        RuleFor(x => x.ClientSecret)
            .Must((request, secret) => !(request.ClientType == "public" && !string.IsNullOrWhiteSpace(secret)))
            .WithMessage("public 客户端不允许配置客户端密钥（请依赖 PKCE）。");
    }
}

public sealed class UpdateClientRequestValidator : AbstractValidator<UpdateClientRequest>
{
    private static readonly string[] AllowedGrantTypes =
    {
        "authorization_code", "refresh_token", "client_credentials", "password", "implicit"
    };

    public UpdateClientRequestValidator()
    {
        RuleFor(x => x.DisplayName).MaximumLength(256)
            .When(x => x.DisplayName is not null);

        RuleForEach(x => x.GrantTypes)
            .Must(t => AllowedGrantTypes.Contains(t))
            .WithMessage($"grantTypes 仅支持：{string.Join(", ", AllowedGrantTypes)}。")
            .When(x => x.GrantTypes is not null);

        RuleFor(x => x.RedirectUris)
            .Must(uris => UriRules.HasValidUris(uris, out _))
            .WithMessage(UriRules.Message)
            .When(x => x.RedirectUris is not null && x.RedirectUris.Count > 0);

        RuleFor(x => x.PostLogoutRedirectUris)
            .Must(uris => UriRules.HasValidUris(uris, out _))
            .WithMessage(UriRules.Message)
            .When(x => x.PostLogoutRedirectUris is not null && x.PostLogoutRedirectUris.Count > 0);
    }
}

/// <summary>重定向地址校验规则（供多个验证器复用）。</summary>
internal static class UriRules
{
    public const string Message = "重定向地址必须是绝对 URI，且非本机地址必须使用 https。";

    public static bool HasValidUris(IEnumerable<string>? uris, out string? invalid)
    {
        invalid = null;
        if (uris is null) return true;

        foreach (var uri in uris)
        {
            if (!IsValidRedirectUri(uri))
            {
                invalid = uri;
                return false;
            }
        }

        return true;
    }

    public static bool IsValidRedirectUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

        // 本机开发允许 http://localhost / http://127.0.0.1
        var isLoopback = parsed.IsLoopback
                         || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase);

        return parsed.Scheme == Uri.UriSchemeHttps || isLoopback;
    }
}
