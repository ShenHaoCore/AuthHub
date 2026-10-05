using System.Net;
using System.Text;
using System.Text.Json;
using AuthHub.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace AuthHub.IntegrationTests;

/// <summary>
/// 错误响应契约（README「错误响应约定」）。
///
/// 这里盯住的核心只有一件事：**400 只能有一种形状**。
///
/// 起因是一个真实的两套契约：请求体绑定失败由 <c>[ApiController]</c> 的
/// <c>InvalidModelStateResponseFactory</c> 产出（英文标题、**没有 code 字段**、
/// traceId 是 W3C traceparent、Content-Type 还是 <c>application/json</c>），
/// 而 FluentValidation 校验失败由 <c>ValidationFilter</c> 产出（中文标题、有 code）。
/// 两条路都返回 400，但字段集不同 —— 下游按 <c>code</c> 分支时会静默漏掉一半。
///
/// 第二件事：Content-Type 必须是 <c>application/problem+json</c>。
/// <c>[Produces("application/json")]</c> 会在结果过滤器阶段覆盖
/// <c>ObjectResult.ContentTypes</c>，所以错误响应必须绕开内容协商直接写响应流
/// （见 <c>ProblemDetailsResult</c>）。
/// </summary>
[Collection(AuthHubFixture.CollectionName)]
public class ApiErrorContractTests
{
    private readonly AuthHubFixture _fixture;

    public ApiErrorContractTests(AuthHubFixture fixture) => _fixture = fixture;

    /// <summary>值能绑定，但业务规则不过 —— 命中 ValidationFilter 里的 FluentValidation。</summary>
    private const string FluentValidationFailingBody =
        """{"userName":"ab","email":"not-an-email","password":"weak"}""";

    /// <summary>空对象：非空引用类型在绑定期就被判为必填 —— 命中 [ApiController] 的模型状态短路。</summary>
    private const string BindingFailingBody = "{}";

    [Theory]
    [InlineData(FluentValidationFailingBody, "fluent-validation")]
    [InlineData(BindingFailingBody, "model-binding")]
    public async Task Any_400_should_use_the_same_problem_shape(string body, string because)
    {
        var response = await _fixture.Client.PostAsync(
            "/api/account/register",
            new StringContent(body, Encoding.UTF8, "application/json"));

        // ① Content-Type 必须是 RFC 7807 规定的那个，而不是被 [Produces] 改回 application/json
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: because);
        response.Content.Headers.ContentType?.MediaType.Should()
            .Be("application/problem+json", because: $"{because} 这条路径也必须走统一出口");

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // ② code 必须在 —— 绑定失败那条路径曾经整块缺这个字段，而它正是下游分支的依据
        problem.GetProperty("code").GetString().Should().Be("ValidationFailed");
        problem.GetProperty("title").GetString().Should().Be("请求参数有误");
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty(
            because: "traceId 要和 Serilog 日志对得上，不能是 W3C 的 traceparent");

        // ③ 字段级错误不能丢（曾经因为按声明类型序列化，errors 被静默丢掉）
        problem.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
    }

    /// <summary>
    /// 绑定失败与校验失败必须是**同一组字段**。上一版这两条路径的字段集不同
    /// （绑定那条没有 code、traceId 形状也不一样），单看任一条都发现不了。
    /// </summary>
    [Fact]
    public async Task Both_400_paths_should_expose_identical_top_level_fields()
    {
        var binding = await ReadProblemAsync(BindingFailingBody);
        var fluent = await ReadProblemAsync(FluentValidationFailingBody);

        var bindingFields = binding.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        var fluentFields = fluent.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);

        bindingFields.Should().Equal(fluentFields,
            because: "同一个 400 不该有两套字段集，下游按 code 分支才靠得住");
    }

    private async Task<JsonElement> ReadProblemAsync(string body)
    {
        var response = await _fixture.Client.PostAsync(
            "/api/account/register",
            new StringContent(body, Encoding.UTF8, "application/json"));

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement;
    }
}
