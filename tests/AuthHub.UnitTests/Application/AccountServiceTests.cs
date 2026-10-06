using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Options;
using AuthHub.Application.Services;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
using AuthHub.Infrastructure.Services;
using AuthHub.UnitTests.Fakes;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AuthHub.UnitTests.Application;

/// <summary>
/// AccountService 登录/注册流程测试。
///
/// UserManager / SignInManager 是 Identity 的具体类，这里用 Moq 直接替掉它们的方法
/// （构造参数用最小可用的桩），从而把测试聚焦在「业务判定分支」上：
/// 用户不存在、账号停用、锁定、需要 MFA、密码错误、成功。
/// </summary>
public class AccountServiceTests
{
    private readonly Mock<UserManager<ApplicationUser>> _users;
    private readonly Mock<SignInManager<ApplicationUser>> _signIn;
    private readonly Mock<ITwoFactorTicketProtector> _tickets = new();
    private readonly Mock<IAuditLogService> _audit = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly Mock<ISmsSender> _sms = new();
    private readonly AccountService _service;

    /// <summary>每个测试用例独享一份用户实例（xUnit 每个用例新建测试类实例）。</summary>
    private readonly ApplicationUser _alice = new()
    {
        Id = "user-1",
        UserName = "alice",
        Email = "alice@example.com",
        DisplayName = "Alice",
        IsActive = true
    };

    public AccountServiceTests()
    {
        _users = new Mock<UserManager<ApplicationUser>>(
            new Mock<IUserStore<ApplicationUser>>().Object,
            Options.Create(new IdentityOptions()),
            new Mock<IPasswordHasher<ApplicationUser>>().Object,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new Mock<ILookupNormalizer>().Object,
            new IdentityErrorDescriber(),
            new Mock<IServiceProvider>().Object,
            NullLogger<UserManager<ApplicationUser>>.Instance);

        _signIn = new Mock<SignInManager<ApplicationUser>>(
            _users.Object,
            new Mock<IHttpContextAccessor>().Object,
            new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>().Object,
            Options.Create(new IdentityOptions()),
            NullLogger<SignInManager<ApplicationUser>>.Instance,
            new Mock<IAuthenticationSchemeProvider>().Object,
            new Mock<IUserConfirmation<ApplicationUser>>().Object);

        _currentUser.SetupGet(u => u.IpAddress).Returns("10.0.0.7");

        // 默认放行：这些是登录成功的副作用，不在本轮断言重点
        _signIn
            .Setup(s => s.SignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _users
            .Setup(u => u.UpdateAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Success);

        _service = new AccountService(
            _users.Object,
            _signIn.Object,
            _audit.Object,
            _tickets.Object,
            _email.Object,
            // 通道用**真实实现** + 打桩的 sender：这样"取哪个联系方式、发什么文案"都被测到，
            // 而不是被一个 mock 通道绕过去。
            new ITwoFactorChannel[]
            {
                new EmailTwoFactorChannel(_email.Object),
                new PhoneTwoFactorChannel(_sms.Object)
            },
            _currentUser.Object,
            // 角色 → 权限用**真实实现** + 出厂默认配置 + 空的运行时覆盖：规则本身由 RolePermissionMapTests
            // 覆盖，这里只需要一个能正常展开的角色映射，不需要把它的行为再 mock 一遍。
            new LayeredRolePermissionMap(
                Options.Create(new RolePermissionOptions()),
                new FakeRolePermissionOverrideStore(),
                NullLogger<LayeredRolePermissionMap>.Instance),
            NullLogger<AccountService>.Instance);
    }

    // ------------------------------------------------------------------ 登录

    [Fact]
    public async Task Login_with_unknown_user_should_return_unauthorized_without_leaking_existence()
    {
        _users.Setup(u => u.FindByNameAsync("ghost")).ReturnsAsync((ApplicationUser?)null);
        _signIn
            .Setup(s => s.CheckPasswordSignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>(), false))
            .ReturnsAsync(SignInResult.Failed);

        var result = await _service.LoginAsync(new LoginRequest("ghost", "Ab@12345"));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
        result.Error.Message.Should().Be("用户名或密码错误。");

        // 防用户名枚举：不存在的用户也要走一次密码哈希校验
        _signIn.Verify(
            s => s.CheckPasswordSignInAsync(It.IsAny<ApplicationUser>(), "Ab@12345", false),
            Times.Once);
        _signIn.Verify(
            s => s.SignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task Login_for_inactive_account_should_be_forbidden()
    {
        _alice.IsActive = false;
        _users.Setup(u => u.FindByNameAsync("alice")).ReturnsAsync(_alice);

        var result = await _service.LoginAsync(new LoginRequest("alice", "Ab@12345"));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Forbidden);
        _signIn.Verify(
            s => s.SignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task Login_when_locked_out_should_report_locked()
    {
        _users.Setup(u => u.FindByNameAsync("alice")).ReturnsAsync(_alice);
        _signIn
            .Setup(s => s.CheckPasswordSignInAsync(_alice, It.IsAny<string>(), true))
            .ReturnsAsync(SignInResult.LockedOut);

        var result = await _service.LoginAsync(new LoginRequest("alice", "Ab@12345"));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.LockedOut);
    }

    [Fact]
    public async Task Login_with_email_identifier_should_look_up_by_email()
    {
        _users.Setup(u => u.FindByEmailAsync("alice@example.com")).ReturnsAsync((ApplicationUser?)null);
        _signIn
            .Setup(s => s.CheckPasswordSignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>(), false))
            .ReturnsAsync(SignInResult.Failed);

        await _service.LoginAsync(new LoginRequest("alice@example.com", "Ab@12345"));

        _users.Verify(u => u.FindByEmailAsync("alice@example.com"), Times.Once);
        _users.Verify(u => u.FindByNameAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Login_when_two_factor_required_should_not_establish_a_session()
    {
        _users.Setup(u => u.FindByNameAsync("alice")).ReturnsAsync(_alice);
        _signIn
            .Setup(s => s.CheckPasswordSignInAsync(_alice, It.IsAny<string>(), true))
            .ReturnsAsync(SignInResult.TwoFactorRequired);
        _tickets.Setup(t => t.Protect("user-1", false)).Returns("opaque-ticket");

        var result = await _service.LoginAsync(new LoginRequest("alice", "Ab@12345"));

        result.IsSuccess.Should().BeTrue();
        result.Value.RequiresTwoFactor.Should().BeTrue();
        result.Value.TwoFactorToken.Should().Be("opaque-ticket");
        result.Value.Succeeded.Should().BeFalse();

        // 关键安全属性：第二因子未通过前绝不建立正式登录会话
        _signIn.Verify(
            s => s.SignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task Successful_login_should_sign_in_and_stamp_last_login()
    {
        _users.Setup(u => u.FindByNameAsync("alice")).ReturnsAsync(_alice);
        _signIn
            .Setup(s => s.CheckPasswordSignInAsync(_alice, "Ab@12345", true))
            .ReturnsAsync(SignInResult.Success);

        var result = await _service.LoginAsync(new LoginRequest("alice", "Ab@12345", RememberMe: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.Succeeded.Should().BeTrue();
        result.Value.DisplayName.Should().Be("Alice");

        _signIn.Verify(
            s => s.SignInAsync(_alice, true, "Password"),
            Times.Once);

        _alice.LastLoginAt.Should().NotBeNull();
        _alice.LastLoginIp.Should().Be("10.0.0.7");
        _users.Verify(u => u.UpdateAsync(_alice), Times.Once);
    }

    [Fact]
    public async Task Login_with_wrong_password_should_return_unauthorized()
    {
        _users.Setup(u => u.FindByNameAsync("alice")).ReturnsAsync(_alice);
        _signIn
            .Setup(s => s.CheckPasswordSignInAsync(_alice, "wrong", true))
            .ReturnsAsync(SignInResult.Failed);

        var result = await _service.LoginAsync(new LoginRequest("alice", "wrong"));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Unauthorized);

        // 失败也要写审计（否则无法做暴力破解分析）
        _audit.Verify(
            a => a.LogLoginFailedAsync("user-1", "alice", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------ MFA 第二阶段

    [Fact]
    public async Task Two_factor_with_expired_or_forged_ticket_should_be_rejected()
    {
        _tickets.Setup(t => t.Unprotect("bad-ticket")).Returns((TwoFactorTicket?)null);

        var result = await _service.CompleteTwoFactorAsync("bad-ticket", "123456", rememberMachine: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
        _signIn.Verify(
            s => s.TwoFactorAuthenticatorSignInAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ 注册

    [Fact]
    public async Task Register_should_map_identity_errors_to_validation_failures()
    {
        _users
            .Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateUserName",
                Description = "用户名已被占用。"
            }));

        var result = await _service.RegisterAsync(
            new RegisterRequest("alice", "alice@example.com", "Ab@12345", null));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.ValidationErrors.Should().NotBeNull();

        // 错误字典以 Identity 的错误码为键，便于客户端按码分支处理
        result.Error.ValidationErrors!.Should().ContainKey("DuplicateUserName");
        result.Error.Message.Should().Contain("用户名已被占用。");

        // 创建失败时不应继续分配角色
        _users.Verify(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Register_should_assign_the_default_role_and_trim_input()
    {
        ApplicationUser? created = null;

        _users
            .Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), "Ab@12345"))
            .Callback<ApplicationUser, string>((user, _) => created = user)
            .ReturnsAsync(IdentityResult.Success);
        _users
            .Setup(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), AuthHubConstants.Roles.User))
            .ReturnsAsync(IdentityResult.Success);
        _users
            .Setup(u => u.GetRolesAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(new List<string> { AuthHubConstants.Roles.User });

        var result = await _service.RegisterAsync(
            new RegisterRequest("  alice  ", "  alice@example.com  ", "Ab@12345", null));

        result.IsSuccess.Should().BeTrue();
        created.Should().NotBeNull();
        created!.UserName.Should().Be("alice");
        created.Email.Should().Be("alice@example.com");
        created.DisplayName.Should().Be("alice");
        created.IsActive.Should().BeTrue();
        created.EmailConfirmed.Should().BeFalse();

        _users.Verify(u => u.AddToRoleAsync(created, AuthHubConstants.Roles.User), Times.Once);
        result.Value.Roles.Should().Contain(AuthHubConstants.Roles.User);
        result.Value.Permissions.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ MFA 下发通道

    private SentEmail? _sentEmail;
    private SentSms? _sentSms;

    private sealed record SentEmail(string To, string Subject, string Body);

    private sealed record SentSms(string To, string Message);

    /// <summary>
    /// 通道名必须与 Identity 的 TwoFactorTokenProvider 名逐字一致（内置的是 "Email" / "Phone"）。
    /// 对不上时 <c>UserManager.GenerateTwoFactorTokenAsync</c> 找不到 provider，
    /// 用户看到的是"验证码发不出去"，而在代码里这两个字符串看起来毫无关系。
    /// </summary>
    [Fact]
    public void Channel_names_should_match_identitys_built_in_provider_names()
    {
        EmailTwoFactorChannel.ProviderName.Should().Be("Email");
        PhoneTwoFactorChannel.ProviderName.Should().Be("Phone");
    }

    [Fact]
    public async Task Send_two_factor_code_over_email_should_deliver_the_generated_code()
    {
        GivenSignedInAlice(code: "654321");
        CaptureEmail();

        var result = await _service.SendTwoFactorCodeAsync("Email");

        result.IsSuccess.Should().BeTrue();
        _sentEmail.Should().NotBeNull();
        _sentEmail!.To.Should().Be("alice@example.com", because: "目标取自通道自己解析的联系方式");
        _sentEmail.Body.Should().Contain("654321", because: "UserManager 生成的验证码必须真的进了文案");

        // 发邮件不该顺带发短信（Moq 的 Verify 不吃 FluentAssertions 的 because:）
        _sms.Verify(
            s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Send_two_factor_code_over_sms_should_deliver_the_generated_code()
    {
        _alice.PhoneNumber = "13900000000";
        GivenSignedInAlice(code: "112233");
        CaptureSms();

        var result = await _service.SendTwoFactorCodeAsync("Phone");

        result.IsSuccess.Should().BeTrue();
        _sentSms.Should().NotBeNull();
        _sentSms!.To.Should().Be("13900000000");
        _sentSms.Message.Should().Contain("112233");

        // 发短信不该顺带发邮件
        _email.Verify(
            s => s.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// 「有没有留联系方式」由通道自己判定，不再散在业务类的 switch 守卫里。
    /// 两条通道都要各自守住 —— 这正是原先最容易只补一半的地方。
    /// </summary>
    [Theory]
    [InlineData("Email")]
    [InlineData("Phone")]
    public async Task Send_two_factor_code_without_a_contact_should_fail_as_validation(string provider)
    {
        _alice.Email = null;
        _alice.PhoneNumber = null;
        GivenSignedInAlice();

        var result = await _service.SendTwoFactorCodeAsync(provider);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Contain(provider);
    }

    [Fact]
    public async Task Send_two_factor_code_for_an_unknown_channel_should_fail_as_validation()
    {
        GivenSignedInAlice();

        // 校验器只要求 Provider 非空，所以业务层必须自己挡住不存在的通道
        var result = await _service.SendTwoFactorCodeAsync("WhatsApp");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Contain("WhatsApp");
        _email.VerifyNoOtherCalls();
        _sms.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Send_two_factor_code_for_an_anonymous_user_should_be_unauthorized()
    {
        _currentUser.SetupGet(u => u.UserId).Returns((string?)null);

        var result = await _service.SendTwoFactorCodeAsync("Email");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
        _email.VerifyNoOtherCalls();
    }

    /// <summary>让 GetCurrentUserAsync 命中 _alice，并把 UserManager 生成的验证码固定下来。</summary>
    private void GivenSignedInAlice(string code = "000000")
    {
        _currentUser.SetupGet(u => u.UserId).Returns("user-1");
        _users.Setup(u => u.FindByIdAsync("user-1")).ReturnsAsync(_alice);
        _users
            .Setup(u => u.GenerateTwoFactorTokenAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(code);
    }

    /// <summary>捕获邮件实际发出的收件人 / 主题 / 正文（真实通道 + 打桩 sender，见构造函数）。</summary>
    private void CaptureEmail() =>
        _email
            .Setup(s => s.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>(
                (to, subject, body, _) => _sentEmail = new SentEmail(to, subject, body))
            .Returns(Task.CompletedTask);

    /// <summary>同上；短信通道没有主题。</summary>
    private void CaptureSms() =>
        _sms
            .Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((to, message, _) => _sentSms = new SentSms(to, message))
            .Returns(Task.CompletedTask);
}
