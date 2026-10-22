using System.Text.Json;
using AuthHub.Application.Options;
using AuthHub.Infrastructure.Security;
using FluentAssertions;

namespace AuthHub.UnitTests.Infrastructure;

/// <summary>
/// CIMD 元数据的解析与校验。
///
/// 这些规则直接决定"谁能被自动注册成客户端"，所以每条失败路径都要有用例：
/// 拒得不清楚，接入方只能靠猜；放过一条，就等于把授权服务器的一部分决定权交给
/// 一个陌生人托管的 JSON。
/// </summary>
public class CimdClientMetadataTests
{
    private const string ClientId = "https://client.example.com/oauth/client-metadata.json";

    private static readonly CimdOptions DefaultOptions = new()
    {
        AllowedScopes = ["openid", "profile", "email", "roles"]
    };

    // ------------------------------------------------------------------ 正常路径

    [Fact]
    public void Valid_metadata_should_be_parsed()
    {
        var parsed = TryParse(Build(Valid()), out var metadata, out var error);

        parsed.Should().BeTrue(error);
        metadata!.ClientId.Should().Be(ClientId);
        metadata.ClientName.Should().Be("Example MCP Client");
        metadata.ApplicationType.Should().Be("web");
        metadata.RedirectUris.Should().Equal("https://client.example.com/callback");
        metadata.Scopes.Should().BeEquivalentTo(new[] { "openid", "profile" });
        metadata.GrantTypes.Should().BeEquivalentTo(new[] { "authorization_code", "refresh_token" });
    }

    [Fact]
    public void Missing_application_type_should_default_to_web()
    {
        var fields = Valid();
        fields.Remove("application_type");

        TryParse(Build(fields), out var metadata, out _).Should().BeTrue();
        metadata!.ApplicationType.Should().Be("web", "缺省取更严格的一侧");
    }

    [Fact]
    public void Missing_scope_should_fall_back_to_the_allow_list()
    {
        var fields = Valid();
        fields.Remove("scope");

        TryParse(Build(fields), out var metadata, out _).Should().BeTrue();
        metadata!.Scopes.Should().BeEquivalentTo(DefaultOptions.AllowedScopes);
    }

    [Fact]
    public void Scope_outside_the_allow_list_should_be_dropped()
    {
        var fields = Valid();
        fields["scope"] = "openid profile admin api:write";

        TryParse(Build(fields), out var metadata, out _).Should().BeTrue();
        metadata!.Scopes.Should().BeEquivalentTo(new[] { "openid", "profile" },
            "元数据由客户端自己托管，它声明什么不能直接信");
    }

    // ------------------------------------------------------------------ 拒绝路径

    [Fact]
    public void Mismatched_client_id_should_be_rejected()
    {
        var fields = Valid();
        fields["client_id"] = "https://attacker.example.com/meta.json";

        TryParse(Build(fields), out _, out var error).Should().BeFalse();
        error.Should().Contain("client_id");
    }

    [Fact]
    public void Missing_client_name_should_be_rejected()
    {
        var fields = Valid();
        fields.Remove("client_name");

        TryParse(Build(fields), out _, out var error).Should().BeFalse(
            "同意页需要展示是哪个应用在要权限，缺了它等于没有知情同意");
        error.Should().Contain("client_name");
    }

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("private_key_jwt")]
    public void Non_public_auth_method_should_be_rejected(string method)
    {
        var fields = Valid();
        fields["token_endpoint_auth_method"] = method;

        TryParse(Build(fields), out _, out var error).Should().BeFalse();
        error.Should().Contain("token_endpoint_auth_method");
    }

    [Fact]
    public void Client_credentials_grant_should_be_rejected()
    {
        var fields = Valid();
        fields["grant_types"] = new[] { "authorization_code", "client_credentials" };

        TryParse(Build(fields), out _, out var error).Should().BeFalse();
        error.Should().Contain("client_credentials");
    }

    [Fact]
    public void Metadata_without_redirect_uris_should_be_rejected()
    {
        var fields = Valid();
        fields.Remove("redirect_uris");

        TryParse(Build(fields), out _, out var error).Should().BeFalse();
        error.Should().Contain("redirect_uris");
    }

    [Fact]
    public void Scopes_entirely_outside_the_allow_list_should_be_rejected()
    {
        var fields = Valid();
        fields["scope"] = "admin api:write";

        TryParse(Build(fields), out _, out var error).Should().BeFalse();
        error.Should().Contain("scope");
    }

    [Fact]
    public void Invalid_json_should_be_rejected()
    {
        TryParse("{ not json", out _, out var error).Should().BeFalse();
        error.Should().Contain("JSON");
    }

    // ------------------------------------------------------------------ 回调地址与 application_type

    [Fact]
    public void Web_client_with_loopback_redirect_uri_should_be_rejected()
    {
        var fields = Valid();
        fields["redirect_uris"] = new[] { "http://localhost:5173/callback" };

        TryParse(Build(fields), out _, out var error).Should().BeFalse(
            "「网页应用」把回调指向用户本机，等于借浏览器打通本地服务");
        error.Should().Contain("回环");
    }

    [Fact]
    public void Native_client_with_loopback_port_should_be_normalized()
    {
        var fields = Valid();
        fields["application_type"] = "native";
        fields["redirect_uris"] = new[] { "http://127.0.0.1:33418/callback" };

        TryParse(Build(fields), out var metadata, out var error).Should().BeTrue(error);

        // 关键：端口必须在入库前去掉。OpenIddict 的 native relaxed 比较只在
        // "请求带非默认端口、库里是默认端口"时才生效，存了 33418 就永远匹配不上
        // 桌面客户端下次启动时的新端口。
        metadata!.RedirectUris.Should().Equal("http://127.0.0.1/callback");
    }

    [Fact]
    public void Web_client_with_plain_http_redirect_uri_should_be_rejected()
    {
        var fields = Valid();
        fields["redirect_uris"] = new[] { "http://client.example.com/callback" };

        TryParse(Build(fields), out _, out var error).Should().BeFalse(
            "非回环的明文 http 会让授权码在网络上裸奔");
        error.Should().Contain("https");
    }

    [Fact]
    public void Redirect_uri_with_fragment_should_be_rejected()
    {
        var fields = Valid();
        fields["redirect_uris"] = new[] { "https://client.example.com/callback#x" };

        TryParse(Build(fields), out _, out var error).Should().BeFalse();
        error.Should().Contain("片段");
    }

    [Fact]
    public void Native_client_may_use_a_custom_scheme()
    {
        var fields = Valid();
        fields["application_type"] = "native";
        fields["redirect_uris"] = new[] { "cursor://anysphere.cursor-retrieval/oauth/callback" };

        TryParse(Build(fields), out var metadata, out var error).Should().BeTrue(error);
        metadata!.RedirectUris.Should().ContainSingle();
    }

    [Fact]
    public void Web_client_may_not_use_a_custom_scheme()
    {
        var fields = Valid();
        fields["redirect_uris"] = new[] { "cursor://anysphere.cursor-retrieval/oauth/callback" };

        TryParse(Build(fields), out _, out var error).Should().BeFalse();
        error.Should().Contain("scheme");
    }

    // ------------------------------------------------------------------ 工具方法

    private static bool TryParse(string document, out CimdClientMetadata? metadata, out string? error) =>
        CimdClientMetadata.TryParse(document, ClientId, DefaultOptions, out metadata, out error);

    private static Dictionary<string, object?> Valid() => new()
    {
        ["client_id"] = ClientId,
        ["client_name"] = "Example MCP Client",
        ["redirect_uris"] = new[] { "https://client.example.com/callback" },
        ["application_type"] = "web",
        ["token_endpoint_auth_method"] = "none",
        ["grant_types"] = new[] { "authorization_code", "refresh_token" },
        ["scope"] = "openid profile"
    };

    private static string Build(Dictionary<string, object?> fields) =>
        JsonSerializer.Serialize(fields);
}
