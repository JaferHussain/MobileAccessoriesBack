using FluentAssertions;
using FluentValidation.TestHelper;
using MoizPos.Api.Controllers;
using MoizPos.Application.Calculations;

namespace MoizPos.UnitTests.Calculations;

/// <summary>
/// The owner's rule: every price is whole rupees — cost, retail, wholesale, and the price a sale
/// charges. Paisa are refused where a price is entered.
/// </summary>
public sealed class WholeRupeesTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1100, true)]
    [InlineData(1100.00, true)]
    [InlineData(99.5, false)]
    [InlineData(0.01, false)]
    public void Knows_a_whole_rupee_from_one_with_paisa(double amount, bool whole)
    {
        WholeRupees.IsWhole((decimal)amount).Should().Be(whole);
    }

    [Fact]
    public void A_price_that_is_not_given_is_not_refused()
    {
        WholeRupees.IsWhole((decimal?)null).Should().BeTrue();
    }

    [Fact]
    public void A_purchase_refuses_paisa_in_its_cost_and_both_selling_prices()
    {
        var result = new CreatePurchaseValidator().TestValidate(new CreatePurchaseRequest
        {
            SupplierId = 1,
            ProductId = 1,
            Quantity = 1,
            UnitCost = 800.50m,
            NewRetailPrice = 1099.99m,
            NewWholesalePrice = 949.25m,
        });

        result.ShouldHaveValidationErrorFor(x => x.UnitCost).WithErrorMessage(WholeRupees.Message);
        result.ShouldHaveValidationErrorFor(x => x.NewRetailPrice).WithErrorMessage(WholeRupees.Message);
        result.ShouldHaveValidationErrorFor(x => x.NewWholesalePrice).WithErrorMessage(WholeRupees.Message);
    }

    [Fact]
    public void A_purchase_in_whole_rupees_is_accepted()
    {
        new CreatePurchaseValidator().TestValidate(new CreatePurchaseRequest
        {
            SupplierId = 1,
            ProductId = 1,
            Quantity = 1,
            UnitCost = 800m,
            NewRetailPrice = 1100m,
            NewWholesalePrice = 950m,
        }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_sale_refuses_a_price_with_paisa()
    {
        var result = new CreateInvoiceValidator().TestValidate(new CreateInvoiceApiRequest
        {
            AmountPaid = 99.5m,
            Items = [new InvoiceLineRequest { ProductId = 1, Quantity = 1, UnitSalePrice = 99.5m }],
        });

        result.Errors.Should().Contain(e => e.ErrorMessage == WholeRupees.Message);
    }
}
