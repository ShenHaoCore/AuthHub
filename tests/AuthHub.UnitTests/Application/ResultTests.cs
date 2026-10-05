using AuthHub.Application.Common;
using FluentAssertions;
using Xunit;

namespace AuthHub.UnitTests.Application;

/// <summary>统一返回值与分页模型的约定测试 —— 它们决定了 Api 层的状态码映射。</summary>
public class ResultTests
{
    [Fact]
    public void Success_should_not_carry_an_error()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Error.Should().Be(Error.None);
    }

    [Theory]
    [InlineData("NotFound")]
    [InlineData("Conflict")]
    [InlineData("Unauthorized")]
    [InlineData("Forbidden")]
    [InlineData("LockedOut")]
    public void Error_factories_should_set_code_equal_to_their_category(string expectedCode)
    {
        var error = expectedCode switch
        {
            "NotFound" => Error.NotFound("x"),
            "Conflict" => Error.Conflict("x"),
            "Unauthorized" => Error.Unauthorized("x"),
            "Forbidden" => Error.Forbidden("x"),
            _ => Error.LockedOut("x")
        };

        error.Code.Should().Be(expectedCode);
        error.Type.ToString().Should().Be(expectedCode);
    }

    [Fact]
    public void Implicit_conversion_should_produce_a_successful_result()
    {
        Result<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Accessing_value_of_a_failed_result_should_throw()
    {
        var result = Result.Failure<int>(Error.NotFound("不存在"));

        result.IsFailure.Should().BeTrue();
        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_successful_result_must_not_carry_an_error()
    {
        var act = () => Result.Failure(Error.None);
        act.Should().Throw<InvalidOperationException>();
    }
}

public class PagedResultTests
{
    private sealed class Query : PagedQuery
    {
    }

    [Fact]
    public void Defaults_should_be_first_page_with_20_items()
    {
        var query = new Query();

        query.Page.Should().Be(1);
        query.PageSize.Should().Be(20);
        query.Skip.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Invalid_page_should_be_normalized_to_first_page(int page)
    {
        var query = new Query { Page = page };

        query.Page.Should().Be(1);
        query.Skip.Should().Be(0);
    }

    [Fact]
    public void Oversized_page_size_should_be_capped_to_prevent_full_table_scans()
    {
        var query = new Query { PageSize = 10_000 };

        query.PageSize.Should().Be(100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_page_size_should_fall_back_to_default(int pageSize)
    {
        new Query { PageSize = pageSize }.PageSize.Should().Be(20);
    }

    [Fact]
    public void Skip_should_be_zero_based_and_never_negative()
    {
        new Query { Page = 3, PageSize = 25 }.Skip.Should().Be(50);
    }

    [Fact]
    public void Total_pages_should_round_up()
    {
        var page = new PagedResult<string>(new[] { "a", "b" }, total: 21, page: 1, pageSize: 10);

        page.TotalPages.Should().Be(3);
        page.HasNext.Should().BeTrue();
        page.HasPrevious.Should().BeFalse();
    }

    [Fact]
    public void Empty_result_should_report_no_pages()
    {
        var page = PagedResult<string>.Empty(page: 1, pageSize: 20);

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(0);
        page.TotalPages.Should().Be(0);
        page.HasNext.Should().BeFalse();
        page.HasPrevious.Should().BeFalse();
    }
}
