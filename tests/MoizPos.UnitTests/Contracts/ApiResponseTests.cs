using FluentAssertions;
using MoizPos.Application.Contracts.Common;
using MoizPos.Domain.Errors;

namespace MoizPos.UnitTests.Contracts;

/// <summary>T023 — the response envelope and paging contract from contracts/conventions.md.</summary>
public sealed class ApiResponseTests
{
    [Fact]
    public void Ok_carries_the_payload_and_no_error()
    {
        var response = ApiResponse<string>.Ok("done");

        response.Success.Should().BeTrue();
        response.Data.Should().Be("done");
        response.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_carries_the_error_and_no_payload()
    {
        var response = ApiResponse<string>.Fail(
            ErrorCodes.InsufficientStock,
            "Not enough stock.",
            [new ErrorDetail("items[1].quantity", "Exceeds available stock (3).")],
            traceId: "0HN7GK2K9J1P4:00000003");

        response.Success.Should().BeFalse();
        response.Data.Should().BeNull();
        response.Error!.Code.Should().Be("INSUFFICIENT_STOCK");
        response.Error.Details.Should().ContainSingle()
            .Which.Field.Should().Be("items[1].quantity");
        response.Error.TraceId.Should().Be("0HN7GK2K9J1P4:00000003");
    }

    [Fact]
    public void Validation_failures_may_carry_several_field_details()
    {
        var response = ApiResponse<string>.Fail(
            ErrorCodes.ValidationFailed,
            "Validation failed.",
            [new ErrorDetail("name", "Required."), new ErrorDetail("salePrice", "Must be >= 0.")]);

        response.Error!.Details.Should().HaveCount(2);
    }
}

public sealed class PagedResultTests
{
    [Fact]
    public void Exposes_the_page_metadata()
    {
        var page = new PagedResult<int>([1, 2, 3], page: 1, pageSize: 25, totalItems: 137);

        page.Items.Should().HaveCount(3);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(25);
        page.TotalItems.Should().Be(137);
        page.TotalPages.Should().Be(6);
    }

    [Fact]
    public void An_exact_multiple_does_not_add_a_trailing_page()
    {
        new PagedResult<int>([], 1, 25, 50).TotalPages.Should().Be(2);
    }

    [Fact]
    public void An_empty_result_has_no_pages()
    {
        new PagedResult<int>([], 1, 25, 0).TotalPages.Should().Be(0);
    }

    [Theory]
    [InlineData(null, null, 1, 25)]     // defaults
    [InlineData(0, 0, 1, 25)]           // nonsense clamped
    [InlineData(-5, -5, 1, 25)]         // negatives clamped
    [InlineData(3, 50, 3, 50)]          // honoured
    [InlineData(2, 500, 2, 100)]        // page size capped
    public void Normalize_clamps_caller_paging_into_range(
        int? page, int? pageSize, int expectedPage, int expectedSize)
    {
        var (normalizedPage, normalizedSize) = PagedResult<int>.Normalize(page, pageSize);

        normalizedPage.Should().Be(expectedPage);
        normalizedSize.Should().Be(expectedSize);
    }

    [Fact]
    public void Rejects_a_page_below_one()
    {
        var act = () => new PagedResult<int>([], page: 0, pageSize: 25, totalItems: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Rejects_a_page_size_above_the_maximum()
    {
        var act = () => new PagedResult<int>([], 1, PagedResult<int>.MaxPageSize + 1, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
