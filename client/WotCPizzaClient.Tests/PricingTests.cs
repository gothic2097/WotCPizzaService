using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WotCPizzaClient.Models;
using WotCPizzaClient.Services;

namespace WotCPizzaClient.Tests;

[TestClass]
public class PricingTests
{
  /// <summary>Provides the server's successful shared pricing examples.</summary>
  public static IEnumerable<object[]> SuccessVectors() => TestData.Read<JsonElement>("pricing-vectors.json")
    .GetProperty("cases").EnumerateArray().Where(c => c.TryGetProperty("expected", out _))
    .Select(c => new object[] { c.GetProperty("name").GetString() ?? "", c.GetProperty("request").GetRawText(), c.GetProperty("expected").GetRawText() });

  /// <summary>Provides the server's shared invalid-promo examples.</summary>
  public static IEnumerable<object[]> ErrorVectors() => TestData.Read<JsonElement>("pricing-vectors.json")
    .GetProperty("cases").EnumerateArray().Where(c => c.TryGetProperty("expectedResultCode", out _))
    .Select(c => new object[] { c.GetProperty("name").GetString() ?? "", c.GetProperty("request").GetRawText(), c.GetProperty("expectedResultCode").GetInt32() });

  /// <summary>Verifies every amount against the server's shared fixtures.</summary>
  [DataTestMethod]
  [DynamicData(nameof(SuccessVectors), DynamicDataSourceType.Method)]
  public void WhenSharedVectorIsPricedThenAllTotalsMatch(string name, string requestJson, string totalsJson)
  {
    var request = JsonSerializer.Deserialize<OrderRequest>(requestJson, WireJson.Options) ?? throw new InvalidOperationException();
    var expected = JsonSerializer.Deserialize<Totals>(totalsJson, WireJson.Options);
    var actual = Pricing.Calculate(TestData.Menu(), request);
    Assert.AreEqual(expected, actual, name);
  }

  /// <summary>Checks promo rejection parity, including both conflict entry orders.</summary>
  [DataTestMethod]
  [DynamicData(nameof(ErrorVectors), DynamicDataSourceType.Method)]
  public void WhenSharedInvalidVectorIsPricedThenResultCodeMatches(string name, string requestJson, int expected)
  {
    var request = JsonSerializer.Deserialize<OrderRequest>(requestJson, WireJson.Options) ?? throw new InvalidOperationException();
    var error = Assert.ThrowsException<OrderValidationException>(() => Pricing.Calculate(TestData.Menu(), request));
    Assert.AreEqual((ResultCode)expected, error.ResultCode, name);
  }

  /// <summary>Verifies exact input conversion without floating-point arithmetic.</summary>
  [DataTestMethod]
  [DataRow("0", false, 0L)]
  [DataRow("2.50", false, 250L)]
  [DataRow("18.25", true, 1825L)]
  [DataRow("100", true, 10000L)]
  public void WhenTipIsValidThenExactCentsOrBasisPointsAreReturned(string text, bool percent, long expected)
  {
    Assert.AreEqual(expected, Pricing.ParseTip(text, percent));
  }

  /// <summary>Rejects fractional cents, negatives, exponents, and out-of-range percentages.</summary>
  [DataTestMethod]
  [DataRow("", false)]
  [DataRow("-1", false)]
  [DataRow("1.001", false)]
  [DataRow("1e2", false)]
  [DataRow("1,50", false)]
  [DataRow("100.01", true)]
  [DataRow("0.001", true)]
  [DataRow("10000000.01", false)]
  public void WhenTipIsInvalidThenItIsRejected(string text, bool percent)
  {
    Assert.ThrowsException<OrderValidationException>(() => Pricing.ParseTip(text, percent));
  }

  /// <summary>Checks that each topping is rounded before summation.</summary>
  [TestMethod]
  public void WhenToppingsHaveFractionalCentsThenEachToppingRoundsDown()
  {
    var menu = TestData.Menu() with { Pizzas = [new(0, "Test size", 101)] };
    var pizza = new PizzaOrder(0, 1, [new(0, [0, 1])]);
    Assert.AreEqual(117L, Pricing.UnitPrice(menu, pizza));
  }

  /// <summary>Ensures duplicate selections cannot inflate prices.</summary>
  [TestMethod]
  public void WhenToppingIsDuplicatedThenOrderIsRejected()
  {
    var request = TestData.Request() with { Pizzas = [new(0, 1, [new(0, [0, 0])])] };
    Assert.ThrowsException<OrderValidationException>(() => Pricing.Calculate(TestData.Menu(), request));
  }

  /// <summary>Enforces quantity boundaries.</summary>
  [DataTestMethod]
  [DataRow(0)]
  [DataRow(101)]
  public void WhenQuantityIsOutsideLimitsThenOrderIsRejected(int count)
  {
    Assert.ThrowsException<OrderValidationException>(() => Pricing.Calculate(TestData.Menu(), TestData.Request((uint)count)));
  }

  /// <summary>Rejects an empty order.</summary>
  [TestMethod]
  public void WhenOrderIsEmptyThenItCannotBePriced()
  {
    var request = TestData.Request() with { Pizzas = [] };
    Assert.ThrowsException<OrderValidationException>(() => Pricing.Calculate(TestData.Menu(), request));
  }

  /// <summary>Enforces the aggregate pizza limit.</summary>
  [TestMethod]
  public void WhenOrderExceedsThousandPizzasThenItIsRejected()
  {
    var request = TestData.Request() with { Pizzas = Enumerable.Repeat(new PizzaOrder(0, 100, []), 11).ToArray() };
    Assert.ThrowsException<OrderValidationException>(() => Pricing.Calculate(TestData.Menu(), request));
  }

  /// <summary>Rejects stale quotes instead of silently repricing.</summary>
  [TestMethod]
  public void WhenMenuVersionIsStaleThenOrderIsRejected()
  {
    var request = TestData.Request() with { MenuVersion = "old" };
    Assert.ThrowsException<OrderValidationException>(() => Pricing.Calculate(TestData.Menu(), request));
  }

  /// <summary>Keeps flat discounts from making the subtotal negative.</summary>
  [TestMethod]
  public void WhenFlatDiscountExceedsSubtotalThenItIsCapped()
  {
    var menu = TestData.Menu() with { Promos = [new("FREE", false, [], DiscountCents: 5000)] };
    var request = TestData.Request() with { PromoCodes = ["FREE"] };
    Assert.AreEqual(0L, Pricing.Calculate(menu, request).TotalAfterTaxAndTipCents);
  }

  /// <summary>Checks ordinal ordering rather than entry ordering for compatible discounts.</summary>
  [TestMethod]
  public void WhenCompatiblePromosAreReversedThenLexicalPricingIsUsed()
  {
    var menu = TestData.Menu() with { Promos = [new("A", false, [], DiscountCents: 100), new("B", true, [], DiscountBasisPoints: 1000)] };
    var request = TestData.Request() with { PromoCodes = ["B", "A"] };
    Assert.AreEqual(360L, Pricing.Calculate(menu, request).TotalBeforeTaxAndTipCents);
  }

  /// <summary>Enforces the bounded final amount.</summary>
  [TestMethod]
  public void WhenFinalTotalExceedsMoneyLimitThenOrderIsRejected()
  {
    var request = TestData.Request() with { TipCents = Pricing.MaxMoney };
    Assert.ThrowsException<OrderValidationException>(() => Pricing.Calculate(TestData.Menu(), request));
  }

  /// <summary>Rejects unsupported deferred promo rules rather than ignoring them.</summary>
  [TestMethod]
  public void WhenMenuUsesCustomerUsageRuleThenItIsRejected()
  {
    var menu = TestData.Menu() with { Promos = [new("LIMIT", false, [new(PromoValidationType.NPerCustomer)])] };
    Assert.ThrowsException<OrderValidationException>(() => Pricing.ValidateMenu(menu));
  }

  /// <summary>Preserves the specified USD formatting on every host culture.</summary>
  [TestMethod]
  public void WhenCentsAreFormattedThenDollarsHaveTwoDecimalPlaces()
  {
    Assert.AreEqual("$5.03", Pricing.Dollars(503));
  }

  /// <summary>Includes the selected topping in the written description.</summary>
  [TestMethod]
  public void WhenOrderHasToppingsThenDescriptionListsTheirPrices()
  {
    var request = TestData.Request() with { Pizzas = [new(0, 2, [new(0, [0])])] };
    StringAssert.Contains(Pricing.Describe(TestData.Menu(), request), "pepperoni — $0.40");
  }
}
