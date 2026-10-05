using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthHub.Api.Pages.Admin;

/// <summary>
/// 我的账户：档案、修改密码、两步验证。
///
/// 设计上刻意分成**三个标签页**而不是长滚动页：改密与 MFA 都是"会改变账号安全状态"的操作，
/// 视觉上与只读信息混在一起容易误操作。标签用纯 CSS/JS 渐进增强，无 JS 时三块内容仍然全部可见可提交。
///
/// 几个实现细节：
///
/// 1) **改密后不需要重新登录**。<c>AccountService.ChangePasswordAsync</c> 内部调用了
///    <c>RefreshSignInAsync</c>：它会刷新安全戳并**重签当前会话的 Cookie**，
///    因此本机不掉线，而其它设备上的旧会话会被安全戳校验拦下。
///
/// 2) **MFA 是两阶段**。`BeginTwoFactorSetupAsync` 只生成密钥（并顺带生成一批恢复码），
///    此时 MFA 仍未启用；必须用 Authenticator 里的 6 位码调用 `EnableTwoFactorAsync`
///    才算真正开启，**那一刻会重新生成一批恢复码**，之前那批作废。
///    所以界面上只在第二阶段展示恢复码 —— 展示第一批等于给用户一堆马上失效的码。
///
/// 3) **密钥用 TempData 跨重定向传递**。`BeginTwoFactorSetupAsync` 是"有副作用的写操作"
///    （会落库 AuthenticatorKey），不能放在 GET 里做；于是走 POST → 302 → GET，
///    密钥/otpauth URI 通过 TempData（Data Protection 加密、HttpOnly、读取即删）带过去。
///    重新渲染时（比如验证码填错）会再调一次 begin —— 它会复用已存在的未确认密钥，
///    不会让用户刚扫进 App 的密钥失效。
/// </summary>
[Authorize(Policy = AuthHubConstants.Policies.Ui.Authenticated)]
public class ProfileModel : PageModel
{
    /// <summary>标签页标识（与视图里的 <c>data-tab</c> 一致）。</summary>
    private const string TabProfile = "profile";
    private const string TabPassword = "password";
    private const string TabMfa = "mfa";

    private readonly IAccountService _account;

    public ProfileModel(IAccountService account)
    {
        _account = account;
    }

    // ------------------------------------------------------------------ 只读状态

    public UserProfileDto? Profile { get; private set; }

    /// <summary>该账号可用的 MFA 通道（Email / Phone / Authenticator）。</summary>
    public IReadOnlyCollection<string> TwoFactorProviders { get; private set; } = Array.Empty<string>();

    /// <summary>当前激活的标签页（服务端渲染初值；有 hash 时由前端接管）。</summary>
    public string ActiveTab { get; private set; } = TabProfile;

    // ------------------------------------------------------------------ 表单字段（POST）

    [BindProperty] public string? CurrentPassword { get; set; }

    [BindProperty] public string? NewPassword { get; set; }

    [BindProperty] public string? ConfirmPassword { get; set; }

    /// <summary>启用 MFA 时输入的 6 位验证码。</summary>
    [BindProperty] public string? EnableCode { get; set; }

    /// <summary>一次性验证码通道（Email / Phone）。</summary>
    [BindProperty] public string? Provider { get; set; }

    // ------------------------------------------------------------------ 操作结果

    public string? PasswordError { get; private set; }

    public string? MfaError { get; private set; }

    /// <summary>绑定用的密钥（仅绑定阶段展示）。</summary>
    public string? SetupSharedKey { get; private set; }

    /// <summary>otpauth:// URI，可直接粘进支持该格式的密码管理器。</summary>
    public string? SetupUri { get; private set; }

    /// <summary>待确认的绑定流程（决定界面显示"开始绑定"还是"输入验证码"）。</summary>
    public bool IsSettingUp => !string.IsNullOrEmpty(SetupSharedKey);

    /// <summary>本次启用成功后返回的恢复码（只展示一次）。</summary>
    public IReadOnlyCollection<string> RecoveryCodes { get; private set; } = Array.Empty<string>();

    public bool HasRecoveryCodes => RecoveryCodes.Count > 0;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "我的账户";
        ViewData["NavKey"] = "profile";

        if (TempData["MfaSetupKey"] is string key)
        {
            SetupSharedKey = key;
            SetupUri = TempData["MfaSetupUri"] as string;
            ActiveTab = TabMfa;
        }

        if (TempData["MfaRecoveryCodes"] is string[] codes)
        {
            RecoveryCodes = codes;
            ActiveTab = TabMfa;
        }

        // TempData 里的提示（其它页面重定向过来时不适用，这里只处理本页的）
        await LoadAsync(cancellationToken);
        return Page();
    }

    // ------------------------------------------------------------------ POST：修改密码

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "我的账户";
        ViewData["NavKey"] = "profile";
        ActiveTab = TabPassword;

        if (string.IsNullOrEmpty(CurrentPassword) || string.IsNullOrEmpty(NewPassword))
        {
            PasswordError = "请填写当前密码与新密码。";
            await LoadAsync(cancellationToken);
            return Page();
        }

        // 两次输入一致性只在界面层校验：它不属于业务规则，不该下沉到 Application 层
        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            PasswordError = "两次输入的新密码不一致。";
            await LoadAsync(cancellationToken);
            return Page();
        }

        var result = await _account.ChangePasswordAsync(
            new ChangePasswordRequest(CurrentPassword, NewPassword),
            cancellationToken);

        if (result.IsFailure)
        {
            // 复杂度不足、密码相同、当前密码错误等都会走到这里，直接把 Identity 的原文呈现给用户
            PasswordError = result.Error.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        TempData["Success"] = "密码已修改。其它设备上的会话会在下次校验时失效，当前设备保持登录。";
        return Redirect("/admin/profile#password");
    }

    // ------------------------------------------------------------------ POST：开始绑定 MFA

    public async Task<IActionResult> OnPostBeginSetupAsync(CancellationToken cancellationToken)
    {
        var result = await _account.BeginTwoFactorSetupAsync(cancellationToken);
        if (result.IsFailure)
        {
            TempData["Error"] = result.Error.Message;
            return Redirect("/admin/profile#mfa");
        }

        TempData["MfaSetupKey"] = result.Value.SharedKey;
        TempData["MfaSetupUri"] = result.Value.AuthenticatorUri;

        return Redirect("/admin/profile#mfa");
    }

    // ------------------------------------------------------------------ POST：确认启用 MFA

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "我的账户";
        ViewData["NavKey"] = "profile";
        ActiveTab = TabMfa;

        var code = EnableCode?.Replace(" ", string.Empty).Trim() ?? string.Empty;

        // 服务层的 EnableTwoFactorAsync 直接吃裸字符串、不经过 FluentValidation，
        // 因此在页面层补一次格式校验，避免把"12"这种输入一路送到 TOTP 校验里
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            MfaError = "Authenticator 的验证码是 6 位数字，请重新输入。";
            await ResumeSetupAsync(cancellationToken);
            return Page();
        }

        var result = await _account.EnableTwoFactorAsync(code, cancellationToken);

        if (result.IsFailure)
        {
            MfaError = result.Error.Message;
            // 失败也要把密钥重新呈现出来：用户可能还没扫完码，或需要在 App 里核对
            await ResumeSetupAsync(cancellationToken);
            return Page();
        }

        TempData["MfaRecoveryCodes"] = result.Value.ToArray();
        TempData["Success"] = "两步验证已启用。请立刻保存下面的恢复码 —— 它们不会再显示第二次。";
        return Redirect("/admin/profile#mfa");
    }

    // ------------------------------------------------------------------ POST：关闭 MFA

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken cancellationToken)
    {
        var result = await _account.DisableTwoFactorAsync(cancellationToken);

        if (result.IsFailure)
        {
            TempData["Error"] = result.Error.Message;
        }
        else
        {
            // 关闭会同时重置 Authenticator 密钥，因此下次开启会得到一个全新的密钥
            TempData["Success"] = "两步验证已关闭，Authenticator 密钥已重置。";
        }

        return Redirect("/admin/profile#mfa");
    }

    // ------------------------------------------------------------------ POST：下发一次性验证码

    public async Task<IActionResult> OnPostSendCodeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            TempData["Error"] = "缺少验证通道。";
            return Redirect("/admin/profile#mfa");
        }

        var result = await _account.SendTwoFactorCodeAsync(Provider, cancellationToken);

        TempData[result.IsFailure ? "Error" : "Success"] = result.IsFailure
            ? result.Error.Message
            : $"已通过 {Provider} 通道下发一次性验证码，5 分钟内有效。";

        return Redirect("/admin/profile#mfa");
    }

    // ------------------------------------------------------------------ 内部辅助

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var profile = await _account.GetProfileAsync(cancellationToken);
        Profile = profile.IsSuccess ? profile.Value : null;

        var providers = await _account.GetTwoFactorProvidersAsync(cancellationToken);
        TwoFactorProviders = providers.IsSuccess ? providers.Value : Array.Empty<string>();
    }

    /// <summary>校验失败后把绑定密钥重新取出来展示（复用未确认的密钥，不会打断已扫码的 App）。</summary>
    private async Task ResumeSetupAsync(CancellationToken cancellationToken)
    {
        var setup = await _account.BeginTwoFactorSetupAsync(cancellationToken);
        if (setup.IsSuccess)
        {
            SetupSharedKey = setup.Value.SharedKey;
            SetupUri = setup.Value.AuthenticatorUri;
        }

        await LoadAsync(cancellationToken);
    }
}
