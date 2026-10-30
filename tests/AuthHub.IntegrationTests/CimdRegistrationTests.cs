using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthHub.Application.Interfaces;
using AuthHub.Domain.Interfaces;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthHub.IntegrationTests;

/// <summary>
/// CIMD（Client ID Metadata Document）动态客户端注册的端到端行为。
/// </summary>
/// <remarks>
/// - 注入假拉取器的原因：真实 CIMD 拉取要求公网 HTTPS 主机，测试宿主跑在本机 ——
///   外部域名请求既不可靠，也等于把 CI 变成出站扫描器。这里验证"拉到元数据之后怎么注册"，
///   SSRF 防护由 <c>CimdAddressPolicyTests</c> 覆盖，两者拼起来才是完整链路。
/// - 未登记的 URL 型 client_id 能直接走通授权码前置校验。
/// - 落库的权限同时含 scope 与 rsrc:（少了后者，#5 修好的 aud 绑定会在这里断掉）。
/// - native + 随机端口回调能匹配到"去掉端口"的注册值。
/// - 元数据不合法 / 被禁用 / 未启用 → 拒。
/// </remarks>
public class CimdRegistrationTests
{
    private const string ClientMetadataUrl = "https://client.example.com/oauth/client-metadata.json";
    private const string McpResource = "https://mcp.example.com/mcp";
    private const string RedirectUri = "https://client.example.com/callback";

    // ------------------------------------------------------------------ ① 自动注册能走通

    [Fact]
    public async Task Unknown_url_client_id_should_be_registered_and_pass_validation()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, fetcher: new StubCimdFetcher(WebMetadata()));
        using var client = CreateClient(host);

        var response = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        // 过了协议校验才会走到业务分支（未登录 → 跳登录页）。
        // 走到这一步说明 client_id 已被解析成可用的应用记录。
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/account/login");
    }

    // ------------------------------------------------------------------ ② 落库的权限

    [Fact]
    public async Task Registered_client_should_carry_scope_and_resource_permissions()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, fetcher: new StubCimdFetcher(WebMetadata()));
        using var client = CreateClient(host);

        await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        using var scope = host.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync(ClientMetadataUrl);

        application.Should().NotBeNull();
        (await manager.GetApplicationTypeAsync(application!)).Should().Be(ApplicationTypes.Web);
        (await manager.GetClientTypeAsync(application!)).Should().Be(ClientTypes.Public);
        (await manager.GetConsentTypeAsync(application!)).Should().Be(ConsentTypes.Explicit,
            "没人审核过的客户端必须让用户看见同意页");

        var permissions = await manager.GetPermissionsAsync(application!);

        permissions.Should().Contain(Permissions.Prefixes.Scope + "openid");
        permissions.Should().Contain(Permissions.Prefixes.Scope + "profile");
        permissions.Should().NotContain(Permissions.Prefixes.Scope + "api:read",
            "元数据申请的 scope 必须与白名单取交集");

        // 少了这一项，客户端一带 resource 就会被「不允许使用指定资源」拒掉
        permissions.Should().Contain(Permissions.Prefixes.Resource + McpResource);
        permissions.Should().Contain(Permissions.GrantTypes.AuthorizationCode);

        var requirements = await manager.GetRequirementsAsync(application!);
        requirements.Should().Contain(Requirements.Features.ProofKeyForCodeExchange);

        var properties = await manager.GetPropertiesAsync(application!);
        properties.Should().ContainKey("authhub:cimd");
        properties["authhub:cimd"].GetBoolean().Should().BeTrue();
    }

    // ------------------------------------------------------------------ ③ native 随机端口

    [Fact]
    public async Task Native_client_with_random_loopback_port_should_match_the_normalized_uri()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, fetcher: new StubCimdFetcher(NativeMetadata()));
        using var client = CreateClient(host);

        // 元数据里声明的是 http://127.0.0.1:33418/callback，入库时去掉端口；请求用的是另一个端口 ——
        // 只有 OpenIddict 的 native relaxed 比较能放行（这条同时验证"归一化确实必要"：不归一化必然 400）。
        const string requestUri = "http://127.0.0.1:52889/callback";

        var response = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, requestUri));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/account/login");
    }

    // ------------------------------------------------------------------ ④ 各种拒绝

    [Fact]
    public async Task Metadata_pointing_at_another_client_id_should_be_rejected()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, fetcher: new StubCimdFetcher(WebMetadata(
            clientId: "https://attacker.example.com/meta.json")));
        using var client = CreateClient(host);

        var response = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_client");
    }

    [Fact]
    public async Task Web_client_with_loopback_redirect_uri_should_be_rejected()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, fetcher: new StubCimdFetcher(WebMetadata(
            redirectUris: ["http://localhost:5173/callback"])));
        using var client = CreateClient(host);

        var response = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, "http://localhost:5173/callback"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_client");
    }

    [Fact]
    public async Task Unreachable_metadata_should_be_rejected_when_nothing_is_cached()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, fetcher: new StubCimdFetcher(
            _ => CimdFetchResult.Failure("拉取元数据超时（5 秒）。")));
        using var client = CreateClient(host);

        var response = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_client");
    }

    [Fact]
    public async Task Url_client_id_should_be_ignored_when_cimd_is_disabled()
    {
        using var factory = CreateFactory(enabled: false);
        using var host = CreateHost(factory, fetcher: new StubCimdFetcher(WebMetadata()));
        using var client = CreateClient(host);

        var response = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        // 默认关闭时行为与升级前完全一致：未知 client_id 被 OpenIddict 拒掉，
        // 且不会产生任何出站请求
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect);
    }

    // ------------------------------------------------------------------ ⑤ 禁用

    [Fact]
    public async Task Disabled_auto_registered_client_should_be_rejected_and_visible_in_admin()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, new StubCimdFetcher(WebMetadata()));
        using var client = CreateClient(host);

        await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        using (var scope = host.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IClientAdminService>();

            var listed = await admin.GetAsync(ClientMetadataUrl);
            listed.IsSuccess.Should().BeTrue();
            listed.Value.IsAutoRegistered.Should().BeTrue("管理后台要能把它与手工登记的客户端区分开");
            listed.Value.MetadataSourceUrl.Should().Be(ClientMetadataUrl);

            (await admin.SetAutoRegisteredDisabledAsync(ClientMetadataUrl, true)).IsSuccess.Should().BeTrue();
        }

        var response = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Should().Be("invalid_client");

        // 关键：禁用是"标记"而不是删记录 —— 删了下次请求会立刻被重新注册回来
        using (var scope = host.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            (await manager.FindByClientIdAsync(ClientMetadataUrl)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Manual_client_should_not_be_toggleable()
    {
        using var factory = CreateFactory();
        using var host = CreateHost(factory, new StubCimdFetcher(WebMetadata()));

        using var scope = host.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IClientAdminService>();

        // spa-client 是种子里的手工客户端
        var result = await admin.SetAutoRegisteredDisabledAsync("spa-client", true);

        result.IsFailure.Should().BeTrue("手工客户端本来就只有管理员能建，禁用按钮对它没有意义");
    }

    // ------------------------------------------------------------------ ⑥ 缓存与降级

    [Fact]
    public async Task Fresh_metadata_should_not_be_fetched_twice()
    {
        using var factory = CreateFactory();
        var fetcher = new StubCimdFetcher(WebMetadata());
        using var host = CreateHost(factory, fetcher);
        using var client = CreateClient(host);

        await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));
        await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        fetcher.CallCount.Should().Be(1, "缓存有效期内不应重复出站请求");
    }

    [Fact]
    public async Task Failed_refresh_should_fall_back_to_the_cached_metadata()
    {
        using var factory = CreateFactory();
        var clock = new MutableClock();
        var fetcher = new StubCimdFetcher(WebMetadata());

        using var host = CreateHost(factory, fetcher, clock);
        using var client = CreateClient(host);

        // 第一次：注册成功并写入缓存
        var first = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // 缓存过期 + 第三方站点挂了
        clock.Advance(TimeSpan.FromHours(25));
        fetcher.Handler = _ => CimdFetchResult.Failure("无法拉取元数据：连接超时。");

        var second = await client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));

        second.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "第三方短暂不可用不该把用户挡在门外 —— 缓存还在就继续用");
        fetcher.CallCount.Should().Be(2, "过期后确实重新拉取过，只是失败了");
    }

    // ------------------------------------------------------------------ ⑦ 并发重取

    [Fact]
    public async Task Concurrent_first_requests_should_fetch_metadata_only_once()
    {
        using var factory = CreateFactory();
        var fetcher = new GatedCimdFetcher(WebMetadata());
        using var host = CreateHost(factory, fetcher);
        using var client = CreateClient(host);

        // 第一个请求会阻塞在拉取器里；此时再发第二个，模拟"两个实例同时发现未注册"
        var first = client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));
        await fetcher.WaitUntilFetchingAsync();

        var second = client.GetAsync(AuthorizeUrl(ClientMetadataUrl, RedirectUri));
        fetcher.Release();

        var responses = await Task.WhenAll(first, second);

        fetcher.CallCount.Should().Be(1,
            "跨实例租约保证同一时刻只有一个请求向第三方站点出站");
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Redirect,
            "排队的那一个要等对方写完再放行，不能因为并发而失败");
    }

    // ------------------------------------------------------------------ 工具方法

    private static AuthHubWebApplicationFactory CreateFactory(bool enabled = true) =>
        new(new Dictionary<string, string>
        {
            ["AuthHub__Cimd__Enabled"] = enabled ? "true" : "false",
            ["AuthHub__Resources__0"] = McpResource
        });

    /// <summary>起一个替换了拉取器（与可选时钟）的宿主。</summary>
    private static WebApplicationFactory<Program> CreateHost(
        AuthHubWebApplicationFactory factory,
        ICimdMetadataFetcher fetcher,
        IClock? clock = null)
    {
        return factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICimdMetadataFetcher>();
            services.AddSingleton(fetcher);

            if (clock is not null)
            {
                services.RemoveAll<IClock>();
                services.AddSingleton(clock);
            }
        }));
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost/")
        });

    private static string AuthorizeUrl(string clientId, string redirectUri)
    {
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile",
            ["state"] = "test-state",
            ["nonce"] = "test-nonce",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };

        return "/connect/authorize?" + string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var document = JsonDocument.Parse(body).RootElement;
        return document.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ------------------------------------------------------------------ 测试数据

    private static CimdFetchResult WebMetadata(
        string clientId = ClientMetadataUrl,
        string[]? redirectUris = null) =>
        CimdFetchResult.Success(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_name"] = "Example MCP Client",
            ["redirect_uris"] = redirectUris ?? [RedirectUri],
            ["application_type"] = "web",
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            // 故意多要 api:read —— 元数据是客户端自己托管的内容，服务端只能按白名单裁剪
            ["scope"] = "openid profile api:read"
        }));

    private static CimdFetchResult NativeMetadata() =>
        CimdFetchResult.Success(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = ClientMetadataUrl,
            ["client_name"] = "Example Desktop MCP Client",
            ["redirect_uris"] = new[] { "http://127.0.0.1:33418/callback" },
            ["application_type"] = "native",
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["scope"] = "openid profile"
        }));

    /// <summary>
    /// 可阻塞的假拉取器：把"第一次出站"卡住，制造两个请求真正重叠的窗口 ——
    /// 直接返回已完成的 Task 会让两个请求完全错开，测不到并发路径。
    /// </summary>
    private sealed class GatedCimdFetcher : ICimdMetadataFetcher
    {
        private readonly CimdFetchResult _result;
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public GatedCimdFetcher(CimdFetchResult result) => _result = result;

        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>等到第一次拉取已经开始（此时它正卡在闸门上）。</summary>
        public Task WaitUntilFetchingAsync() => _started.Task;

        /// <summary>放行，让卡住的拉取返回元数据。</summary>
        public void Release() => _release.TrySetResult();

        public async Task<CimdFetchResult> FetchAsync(Uri metadataUri, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken);

            return _result;
        }
    }

    /// <summary>可编程的假拉取器：记录调用次数，行为可在用例中途替换。</summary>
    private sealed class StubCimdFetcher : ICimdMetadataFetcher
    {
        public StubCimdFetcher(CimdFetchResult result) => Handler = _ => result;

        public StubCimdFetcher(Func<Uri, CimdFetchResult> handler) => Handler = handler;

        public Func<Uri, CimdFetchResult> Handler { get; set; }

        public int CallCount { get; private set; }

        public Task<CimdFetchResult> FetchAsync(Uri metadataUri, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Handler(metadataUri));
        }
    }

    /// <summary>可推进的时钟 —— 用来在用例内模拟"缓存过期"。</summary>
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan delta) => UtcNow += delta;
    }
}
