using AuthHub.Application.Common;
using AuthHub.Application.DTOs.Account;
using AuthHub.Application.Interfaces;
using AuthHub.Application.Services;
using AuthHub.Domain.Constants;
using AuthHub.Domain.Entities;
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
            new Mock<IEmailSender>().Object,
            new Mock<ISmsSender>().Object,
            _currentUser.Object,
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
}
