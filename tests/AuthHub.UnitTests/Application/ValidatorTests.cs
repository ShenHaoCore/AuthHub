using AuthHub.Application.DTOs.Account;
using AuthHub.Application.DTOs.Clients;
using AuthHub.Application.Validators;
using FluentAssertions;
using Xunit;

namespace AuthHub.UnitTests.Application;

/// <summary>
/// 验证器测试。
/// 这些规则与 IdentityOptions 的密码策略保持一致，是“接口层快速失败”的第一道闸门，
/// 因此任何调整都必须同步两组断言。
/// </summary>
public class AccountValidatorTests
{
    private readonly RegisterRequestValidator _register = new();
    private readonly LoginRequestValidator _login = new();
    private readonly ChangePasswordRequestValidator _changePassword = new();

    [Theory]
    [InlineData("Ab@12345")]        // 刚好 8 位
    [InlineData("Str0ng!Passw0rd")]
    public void Valid_registration_should_pass(string password)
    {
        var result = _register.Validate(new RegisterRequest("alice", "alice@example.com", password, null));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Theory]
    [InlineData("Ab@1", "长度不足 8 位")]
    [InlineData("abcdefg@1", "缺少大写字母")]
    [InlineData("ABCDEFG@1", "缺少小写字母")]
    [InlineData("Abcdefgh@", "缺少数字")]
    [InlineData("Abcdefg1", "缺少特殊字符")]
    public void Weak_password_should_be_rejected(string password, string reason)
    {
        var result = _register.Validate(new RegisterRequest("alice", "alice@example.com", password, null));

        result.IsValid.Should().BeFalse(reason);
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RegisterRequest.Password));
    }

    [Theory]
    [InlineData("ab")]                 // 太短
    [InlineData("alice smith")]        // 含空格
    [InlineData("alice/../root")]      // 含路径字符
    public void Invalid_user_name_should_be_rejected(string userName)
    {
        var result = _register.Validate(new RegisterRequest(userName, "alice@example.com", "Ab@12345", null));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("alice@")]
    [InlineData("")]
    public void Invalid_email_should_be_rejected(string email)
    {
        var result = _register.Validate(new RegisterRequest("alice", email, "Ab@12345", null));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Login_request_requires_both_identifier_and_password()
    {
        _login.Validate(new LoginRequest("", "x")).IsValid.Should().BeFalse();
        _login.Validate(new LoginRequest("alice", "")).IsValid.Should().BeFalse();
        _login.Validate(new LoginRequest("alice", "x")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Change_password_should_enforce_the_same_complexity()
    {
        _changePassword.Validate(new ChangePasswordRequest("Old@12345", "New@12345")).IsValid.Should().BeTrue();
        _changePassword.Validate(new ChangePasswordRequest("Old@12345", "weak")).IsValid.Should().BeFalse();
        _changePassword.Validate(new ChangePasswordRequest("", "New@12345")).IsValid.Should().BeFalse();
    }
}

/// <summary>
/// 客户端校验测试。
/// 重点在重定向地址白名单规则：它是防开放重定向的关键一环。
/// </summary>
public class ClientValidatorTests
{
    private readonly CreateClientRequestValidator _validator = new();

    private static CreateClientRequest Request(
        IReadOnlyCollection<string>? redirectUris = null,
        string? clientType = null,
        string? clientSecret = null,
        IReadOnlyCollection<string>? grantTypes = null)
        => new("my-client-01", "示例", clientSecret, "web", clientType, redirectUris, null, grantTypes, null);

    [Theory]
    [InlineData("https://app.example.com/callback")]
    [InlineData("http://localhost:3000/callback")]
    [InlineData("http://127.0.0.1:3000/callback")]
    public void Secure_or_loopback_redirect_uris_should_pass(string uri)
    {
        _validator.Validate(Request(new[] { uri })).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://app.example.com/callback")]  // 非本机必须 https
    [InlineData("/callback")]                        // 必须是绝对 URI
    [InlineData("app.example.com/callback")]         // 缺少 scheme
    [InlineData("javascript:alert(1)")]              // 危险 scheme
    public void Insecure_or_relative_redirect_uris_should_be_rejected(string uri)
    {
        var result = _validator.Validate(Request(new[] { uri }));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateClientRequest.RedirectUris));
    }

    [Fact]
    public void Public_client_must_not_carry_a_secret()
    {
        var result = _validator.Validate(Request(clientType: "public", clientSecret: "a-very-long-secret"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateClientRequest.ClientSecret));
    }

    [Fact]
    public void Public_client_without_secret_should_pass()
    {
        _validator.Validate(Request(clientType: "public")).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("ab")]              // 少于 3 位
    [InlineData("my client")]       // 含空格
    public void Invalid_client_id_should_be_rejected(string clientId)
    {
        var request = new CreateClientRequest(clientId);

        _validator.Validate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Unsupported_grant_type_should_be_rejected()
    {
        var result = _validator.Validate(Request(grantTypes: new[] { "authorization_code", "device_code" }));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("GrantTypes"));
    }

    [Fact]
    public void Supported_grant_types_should_pass()
    {
        _validator.Validate(Request(grantTypes: new[] { "authorization_code", "refresh_token", "client_credentials" }))
            .IsValid.Should().BeTrue();
    }
}
