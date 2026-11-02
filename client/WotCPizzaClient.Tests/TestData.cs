using System.IO;
using System.Text.Json;
using WotCPizzaClient.Models;
using WotCPizzaClient.Services;

namespace WotCPizzaClient.Tests;

internal static class TestData
{
  internal static Menu Menu() => Read<Menu>("menu.json");

  internal static T Read<T>(string name)
  {
    using var stream = typeof(TestData).Assembly.GetManifestResourceStream("WotCPizzaClient.Tests.Fixtures." + name)
      ?? throw new InvalidDataException(name);
    return JsonSerializer.Deserialize<T>(stream, WireJson.Options) ?? throw new InvalidDataException(name);
  }

  internal static OrderRequest Request(uint count = 1) => new()
  {
    OrderId = "00000000-0000-0000-0000-000000000001", MenuVersion = "1",
    Pizzas = [new(0, count, [])], PromoCodes = []
  };

  internal static CustomerOrder Snapshot(OrderRequest? request = null, ulong revision = 1, OrderStatus status = OrderStatus.NotStarted)
  {
    var draft = request ?? Request();
    return new()
    {
      OrderId = draft.OrderId, MenuVersion = draft.MenuVersion, Pizzas = draft.Pizzas,
      PromoCodes = draft.PromoCodes, TipCents = draft.TipCents, TipBasisPoints = draft.TipBasisPoints,
      CustomerId = 0, Status = status, Revision = revision, TimeTillReady = status == OrderStatus.NotStarted ? 60u : 0u,
      Totals = Pricing.Calculate(Menu(), draft), Description = Pricing.Describe(Menu(), draft)
    };
  }
}

internal sealed class ScriptedService : IPizzaService
{
  internal List<string> Requests { get; } = [];
  internal Queue<Func<ApiResponse>> Responses { get; } = new();
  internal ApiRequest LastRequest => JsonSerializer.Deserialize<ApiRequest>(Requests[^1], WireJson.Options)
    ?? throw new InvalidDataException();

  public Task<ApiResponse> SendAsync(string json, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Requests.Add(json);
    return Task.FromResult(Responses.Dequeue()());
  }

  internal void Enqueue(ApiResponse response) => Responses.Enqueue(() => response);

  internal void SignInWith(params CustomerOrder[] orders)
  {
    Enqueue(new() { ResultCode = ResultCode.Success, Menu = TestData.Menu() });
    Enqueue(new() { ResultCode = ResultCode.Success, CustomerOrders = orders });
  }
}

internal sealed class TestDialogs : IClientDialogs
{
  internal PizzaOrder? Pizza { get; set; } = new(0, 1, []);
  internal List<string> Messages { get; } = [];
  internal bool ConfirmResult { get; set; } = true;
  internal bool RetryResult { get; set; } = true;
  internal int RetryCount { get; private set; }
  internal bool Exited { get; private set; }
  internal Action? OnRetry { get; set; }
  internal Action? OnExit { get; set; }

  public void Show(string message) => Messages.Add(message);

  public bool Confirm(string message)
  {
    Messages.Add(message);
    return ConfirmResult;
  }

  public bool Retry(string detail)
  {
    RetryCount++;
    OnRetry?.Invoke();
    return RetryResult;
  }

  public PizzaOrder? EditPizza(Menu menu, PizzaOrder? pizza) => Pizza;

  public void ShowReceipt(string description) => Messages.Add(description);

  public void Exit()
  {
    Exited = true;
    OnExit?.Invoke();
  }
}
