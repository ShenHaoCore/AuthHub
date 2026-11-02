using System.Globalization;
using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Enums;
using Microsoft.AspNetCore.Identity;

namespace AuthHub.Application.Services;

/// <summary>
/// MFA 生命周期管理：Authenticator 开启 / 关闭、恢复码、验证码通道（AccountService 的 partial 部分）。
/// </summary>
public partial class AccountService
{
    private const string AuthenticatorIssuer = "AuthHub";

    public async Task<Result<TwoFactorSetupResponse>> BeginTwoFactorSetupAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure<TwoFactorSetupResponse>(Error.Unauthorized("未登录。"));
        }

        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            key = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        if (string.IsNullOrEmpty(key))
        {
            return Result.Failure<TwoFactorSetupResponse>(Error.Failure("TwoFactorKey", "无法生成 Authenticator 密钥。"));
        }

        var accountName = user.Email ?? user.UserName ?? user.Id;
        var uri = string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6&period=30",
            Uri.EscapeDataString(AuthenticatorIssuer),
            Uri.EscapeDataString(accountName),
            key);

        // 恢复码在这里一并生成：用户确认 6 位码之前不会被启用
        var recoveryCodes = await GenerateRecoveryCodesInternalAsync(user);

        return Result.Success(new TwoFactorSetupResponse(key, uri, recoveryCodes));
    }

    public async Task<Result<IReadOnlyCollection<string>>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure<IReadOnlyCollection<string>>(Error.Unauthorized("未登录。"));
        }

        var isValid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            code.Replace(" ", string.Empty));

        if (!isValid)
        {
            await _audit.LogAsync(
                new AuditEntry(AuditActionType.TwoFactorFailed, false, user.Id, user.UserName, Details: "启用 MFA 时验证码校验失败"),
                cancellationToken);

            return Result.Failure<IReadOnlyCollection<string>>(Error.Unauthorized("验证码不正确，请确认 App 时间同步后重试。"));
        }

        await _userManager.SetTwoFactorEnabledAsync(user, true);

        // 启用时重置恢复码，确保返回给用户的就是当前有效的那一批
        var recoveryCodes = await GenerateRecoveryCodesInternalAsync(user);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.TwoFactorEnabled, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success<IReadOnlyCollection<string>>(recoveryCodes);
    }

    public async Task<Result> DisableTwoFactorAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure(Error.Unauthorized("未登录。"));
        }

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);

        await _audit.LogAsync(
            new AuditEntry(AuditActionType.TwoFactorDisabled, true, user.Id, user.UserName),
            cancellationToken);

        return Result.Success();
    }

    public async Task<Result> SendTwoFactorCodeAsync(string provider, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure(Error.Unauthorized("未登录。"));
        }

        // 顺序刻意与改造前一致：**先**生成验证码，**再**找通道。
        // 反过来写会在 provider 非法时改变 UserManager 的调用时机 —— 那属于未经请求的行为变更。
        var code = await _userManager.GenerateTwoFactorTokenAsync(user, provider);

        if (!_twoFactorChannels.TryGetValue(provider, out var channel) ||
            channel.ResolveTarget(user) is not { } target)
        {
            // 「通道不存在」与「通道存在但用户没留联系方式」共用同一条消息，是既有响应契约。
            // 想分开写得更准，要先确认调用方/文档没有依赖这句原文。
            return Result.Failure(Error.Validation($"当前账号未配置 {provider} 通道所需的联系方式。"));
        }

        await channel.SendAsync(target, code, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyCollection<string>>> GetTwoFactorProvidersAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Result.Failure<IReadOnlyCollection<string>>(Error.Unauthorized("未登录。"));
        }

        var providers = await _userManager.GetValidTwoFactorProvidersAsync(user);
        return Result.Success<IReadOnlyCollection<string>>(providers.ToArray());
    }
}
