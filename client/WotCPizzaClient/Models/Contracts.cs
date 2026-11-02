using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotCPizzaClient.Models;

public enum ResultCode
{
  Fail = -1,
  Success = 0,
  ConnectionError,
  ConnectionInvalidCall,
  ConnectionInvalidInput,
  OrderAlreadyCreated,
  OrderAlreadyStarted,
  PromoInvalid,
  PromoValid
}

public enum OrderStatus
{
  NotStarted,
  Started,
  ReadyForPickUp,
  Delivered,
  Cancelled
}

public enum PromoValidationType
{
  OnePromoPerOrder,
  NotValidWithOtherPromos,
  NPerCustomer,
  TotalOverN
}

public enum FunctionId
{
  GetMenu,
  GetOrder,
  GetAllOrders,
  PlaceOrder,
  GetOrderStatus,
  UpdateOrder,
  CancelOrder,
  IsPromoValid,
  CompleteOrder
}

public sealed record Menu
{
  public required string MenuVersion { get; init; }
  public required long TaxBasisPoints { get; init; }
  public required PizzaSize[] Pizzas { get; init; }
  public required ToppingCategory[] Toppings { get; init; }
  public required Promo[] Promos { get; init; }
}

public sealed record PizzaSize(uint Code, string Name, long CostCents);
public sealed record ToppingItem(uint Code, string Name);
public sealed record ToppingCategory(uint Code, string Name, long CostBasisPoints, ToppingItem[] Toppings);
public sealed record PromoValidation(PromoValidationType Type, string[]? ArrayOfStrings = null, long ThresholdCents = 0);
public sealed record Promo(string Code, bool IsPercentOff, PromoValidation[] Validations, long DiscountBasisPoints = 0, long DiscountCents = 0);
public sealed record ToppingSelection(uint ToppingBaseCode, uint[] Toppings);
public sealed record PizzaOrder(uint PizzaCode, uint Count, ToppingSelection[] Toppings);

public sealed record OrderRequest
{
  public required string OrderId { get; init; }
  public required string MenuVersion { get; init; }
  public required PizzaOrder[] Pizzas { get; init; }
  public required string[] PromoCodes { get; init; }
  public long TipCents { get; init; }
  public long TipBasisPoints { get; init; }
}

public sealed record Totals(long SubtotalCents, long DiscountCents, long TotalBeforeTaxAndTipCents,
  long TaxCents, long TipCents, long TotalAfterTaxAndTipCents);

public sealed record CustomerOrder
{
  public required string OrderId { get; init; }
  public required string MenuVersion { get; init; }
  public required PizzaOrder[] Pizzas { get; init; }
  public required string[] PromoCodes { get; init; }
  public long TipCents { get; init; }
  public long TipBasisPoints { get; init; }
  public required uint CustomerId { get; init; }
  public required OrderStatus Status { get; init; }
  public required ulong Revision { get; init; }
  public required uint TimeTillReady { get; init; }
  public required Totals Totals { get; init; }
  public required string Description { get; init; }

  /// <summary>Creates a detached request for editing without altering the accepted snapshot.</summary>
  public OrderRequest ToRequest() => new()
  {
    OrderId = OrderId, MenuVersion = MenuVersion, TipCents = TipCents, TipBasisPoints = TipBasisPoints,
    PromoCodes = [.. PromoCodes], Pizzas = Pizzas.Select(p => p with
    {
      Toppings = p.Toppings.Select(t => t with { Toppings = [.. t.Toppings] }).ToArray()
    }).ToArray()
  };
}

public sealed record RequestInput(uint CustomerId, string? OrderId = null, OrderRequest? CustomerOrder = null,
  ulong? ExpectedRevision = null, string? PromoCode = null);
public sealed record ApiRequest([property: JsonPropertyName("FunctionId")] FunctionId FunctionId,
  [property: JsonPropertyName("Input")] RequestInput? Input = null);

public sealed record ApiResponse
{
  public required ResultCode ResultCode { get; init; }
  public string? Error { get; init; }
  public string? Message { get; init; }
  public Menu? Menu { get; init; }
  public CustomerOrder? CustomerOrder { get; init; }
  public CustomerOrder[]? CustomerOrders { get; init; }
  public OrderStatus? Status { get; init; }
  public ulong? Revision { get; init; }
  public uint? TimeTillReady { get; init; }
  public Totals? Totals { get; init; }
}

public static class WireJson
{
  public static JsonSerializerOptions Options { get; } = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };
}
