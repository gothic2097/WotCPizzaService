using System.Globalization;
using System.Text;
using WotCPizzaClient.Models;
using WotCPizzaClient.Resources;

namespace WotCPizzaClient.Services;

public sealed class OrderValidationException(string message, ResultCode resultCode = ResultCode.ConnectionInvalidInput)
  : ArgumentException(message)
{
  public ResultCode ResultCode { get; } = resultCode;
}

public static class Pricing
{
  public const long MaxMoney = 1_000_000_000;
  public const long RateScale = 10_000;

  /// <summary>Formats integer cents as dollars independently of the machine's currency.</summary>
  public static string Dollars(long cents) => "$" + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

  /// <summary>Parses dollars or percentage points without silently rounding user input.</summary>
  public static long ParseTip(string text, bool percent)
  {
    if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
        value < 0 || value > (percent ? 100m : MaxMoney / 100m) || value * 100 != decimal.Truncate(value * 100))
      throw Invalid(percent ? "PercentInvalid" : "MoneyInvalid");
    return (long)(value * 100);
  }

  /// <summary>Rejects unsupported menu data rather than producing a misleading quote.</summary>
  public static void ValidateMenu(Menu menu)
  {
    ArgumentNullException.ThrowIfNull(menu);
    if (string.IsNullOrWhiteSpace(menu.MenuVersion) || menu.Pizzas is null || menu.Pizzas.Length == 0 ||
        menu.Toppings is null || menu.Promos is null || menu.TaxBasisPoints is < 0 or > RateScale ||
        menu.Pizzas.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.CostCents is < 1 or > MaxMoney) ||
        menu.Pizzas.Select(p => p.Code).Distinct().Count() != menu.Pizzas.Length ||
        menu.Toppings.Any(t => t is null || string.IsNullOrWhiteSpace(t.Name) || t.CostBasisPoints is < 0 or > RateScale ||
          t.Toppings is null || t.Toppings.Any(i => i is null || string.IsNullOrWhiteSpace(i.Name)) ||
          t.Toppings.Select(i => i.Code).Distinct().Count() != t.Toppings.Length) ||
        menu.Toppings.Select(t => t.Code).Distinct().Count() != menu.Toppings.Length ||
        menu.Promos.Length > 20 || menu.Promos.Any(p => p is null || string.IsNullOrWhiteSpace(p.Code) || p.Validations is null ||
          (p.IsPercentOff ? p.DiscountBasisPoints is < 0 or > RateScale : p.DiscountCents is < 0 or > MaxMoney)) ||
        menu.Promos.Select(p => p.Code).Distinct(StringComparer.Ordinal).Count() != menu.Promos.Length)
      throw Invalid("InvalidMenu");
    foreach (var promo in menu.Promos)
      foreach (var rule in promo.Validations)
      {
        if (rule is null || !Enum.IsDefined(rule.Type) || rule.Type == PromoValidationType.NPerCustomer || rule.ThresholdCents < 0)
          throw Invalid("InvalidMenu");
        if (rule.Type == PromoValidationType.NotValidWithOtherPromos &&
            (rule.ArrayOfStrings is null || rule.ArrayOfStrings.Any(c => c == promo.Code || !menu.Promos.Any(p => p.Code == c))))
          throw Invalid("InvalidMenu");
      }
  }

  /// <summary>Computes a validated quote using the server's per-stage round-down rules.</summary>
  public static Totals Calculate(Menu menu, OrderRequest request)
  {
    ArgumentNullException.ThrowIfNull(menu);
    ArgumentNullException.ThrowIfNull(request);
    if (request.MenuVersion != menu.MenuVersion) throw Invalid("StaleMenu");
    if (request.Pizzas is null || request.Pizzas.Length is < 1 or > 100) throw Invalid("LinesInvalid");
    if (request.Pizzas.Any(p => p is null || p.Count is < 1 or > 100)) throw Invalid("QuantityInvalid");
    if (request.Pizzas.Sum(p => (long)p.Count) > 1000) throw Invalid("LinesInvalid");
    if (request.TipCents is < 0 or > MaxMoney || request.TipBasisPoints is < 0 or > RateScale ||
        (request.TipCents != 0 && request.TipBasisPoints != 0)) throw Invalid("TipModes");
    long subtotal = 0;
    foreach (var pizza in request.Pizzas) subtotal = Bounded(subtotal + UnitPrice(menu, pizza) * pizza.Count);
    if (request.PromoCodes is null || request.PromoCodes.Length > 20) throw Invalid("InvalidMenu");
    var codes = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var code in request.PromoCodes)
    {
      if (string.IsNullOrWhiteSpace(code) || !menu.Promos.Any(p => p.Code == code)) throw PromoError("PromoUnknown", code ?? "");
      if (!codes.Add(code)) throw PromoError("PromoDuplicate", code);
    }
    foreach (var code in codes)
    {
      var promo = menu.Promos.Single(p => p.Code == code);
      foreach (var rule in promo.Validations)
      {
        if (rule.Type == PromoValidationType.TotalOverN && subtotal <= rule.ThresholdCents)
          throw PromoError("PromoThreshold", code, Dollars(rule.ThresholdCents));
        if (rule.Type == PromoValidationType.NotValidWithOtherPromos)
          foreach (var conflict in rule.ArrayOfStrings ?? [])
            if (codes.Contains(conflict)) throw PromoError("PromoConflict", code, conflict);
        if (rule.Type == PromoValidationType.NPerCustomer || !Enum.IsDefined(rule.Type)) throw Invalid("InvalidMenu");
      }
    }
    long discounted = subtotal;
    foreach (var code in codes)
    {
      var promo = menu.Promos.Single(p => p.Code == code);
      discounted -= promo.IsPercentOff ? discounted * promo.DiscountBasisPoints / RateScale : Math.Min(discounted, promo.DiscountCents);
    }
    long tax = discounted * menu.TaxBasisPoints / RateScale;
    long tip = request.TipBasisPoints != 0 ? (discounted + tax) * request.TipBasisPoints / RateScale : request.TipCents;
    return new(subtotal, subtotal - discounted, discounted, tax, tip, Bounded(discounted + tax + tip));
  }

  /// <summary>Calculates one pizza's base plus individually rounded topping prices.</summary>
  public static long UnitPrice(Menu menu, PizzaOrder pizza)
  {
    var size = menu.Pizzas.SingleOrDefault(p => p.Code == pizza.PizzaCode) ?? throw Invalid("UnknownPizza");
    long unit = size.CostCents;
    HashSet<uint> categories = [];
    if (pizza.Toppings is null) throw Invalid("UnknownTopping");
    foreach (var selection in pizza.Toppings)
    {
      if (selection is null || selection.Toppings is null || !categories.Add(selection.ToppingBaseCode)) throw Invalid("UnknownTopping");
      var category = menu.Toppings.SingleOrDefault(t => t.Code == selection.ToppingBaseCode) ?? throw Invalid("UnknownTopping");
      HashSet<uint> toppings = [];
      foreach (var code in selection.Toppings)
      {
        if (!toppings.Add(code) || !category.Toppings.Any(t => t.Code == code)) throw Invalid("UnknownTopping");
        unit = Bounded(unit + size.CostCents * category.CostBasisPoints / RateScale);
      }
    }
    return unit;
  }

  /// <summary>Builds a readable draft description; accepted receipts always come from the server.</summary>
  public static string Describe(Menu menu, OrderRequest request)
  {
    var totals = Calculate(menu, request);
    var text = new StringBuilder();
    foreach (var pizza in request.Pizzas)
    {
      var size = menu.Pizzas.Single(p => p.Code == pizza.PizzaCode);
      var unit = UnitPrice(menu, pizza);
      text.AppendLine(Text.Get("PizzaLine", pizza.Count, size.Name, Dollars(unit), Dollars(unit * pizza.Count)));
      text.AppendLine("  " + Text.Get("BaseLine", Dollars(size.CostCents)));
      bool any = false;
      foreach (var selection in pizza.Toppings)
      {
        var category = menu.Toppings.Single(t => t.Code == selection.ToppingBaseCode);
        foreach (var code in selection.Toppings)
        {
          text.AppendLine("  " + Text.Get("ToppingLine", category.Toppings.Single(t => t.Code == code).Name,
            Dollars(size.CostCents * category.CostBasisPoints / RateScale)));
          any = true;
        }
      }
      if (!any) text.AppendLine("  " + Text.Get("NoToppings"));
    }
    foreach (var (key, cents) in new[] { ("Subtotal", totals.SubtotalCents), ("Discount", totals.DiscountCents),
      ("BeforeTax", totals.TotalBeforeTaxAndTipCents), ("Tax", totals.TaxCents), ("Tip", totals.TipCents), ("Total", totals.TotalAfterTaxAndTipCents) })
      text.AppendLine(Text.Get("ReceiptLine", Text.Get(key), Dollars(cents)));
    if (request.PromoCodes.Length > 0) text.AppendLine(string.Join(", ", request.PromoCodes));
    return text.ToString();
  }

  private static long Bounded(long value) => value is < 0 or > MaxMoney ? throw Invalid("AmountLimit") : value;
  private static OrderValidationException Invalid(string key) => new(Text.Get(key));
  private static OrderValidationException PromoError(string key, params object[] args) => new(Text.Get(key, args), ResultCode.PromoInvalid);
}
