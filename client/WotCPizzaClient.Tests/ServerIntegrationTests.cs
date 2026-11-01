using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WotCPizzaClient.Models;
using WotCPizzaClient.Services;

namespace WotCPizzaClient.Tests;

[TestClass]
[TestCategory("ServerIntegration")]
public class ServerIntegrationTests
{
  public TestContext? TestContext { get; set; }
  private TestContext GetContext() => TestContext ?? throw new InvalidOperationException("MSTest did not supply a test context.");

  /// <summary>Checks that a separate C++ process agrees with the client's integer quote.</summary>
  [TestMethod]
  public async Task WhenOrderIsPlacedThenServerAndClientTotalsMatchAsync()
  {
    await using var server = await IsolatedServer.StartAsync(GetContext());
    var menu = (await server.CallAsync(ApiCalls.GetMenu())).Menu ?? throw new InvalidDataException();
    var request = TestData.Request() with
    {
      Pizzas = [new(2, 2, [new(0, [0]), new(1, [1])])], PromoCodes = ["ILOVEPIZZA"], TipBasisPoints = 1500
    };
    var response = await server.CallAsync(ApiCalls.PlaceOrder(0, request));
    Assert.AreEqual(Pricing.Calculate(menu, request), response.CustomerOrder?.Totals);
  }

  /// <summary>Retries must not create another order or reprice an accepted order.</summary>
  [TestMethod]
  public async Task WhenPlacementIsRepeatedThenServerReturnsAlreadyCreatedAsync()
  {
    await using var server = await IsolatedServer.StartAsync(GetContext());
    var request = ApiCalls.PlaceOrder(0, TestData.Request());
    await server.CallAsync(request);
    var retry = await server.CallAsync(request);
    Assert.AreEqual(ResultCode.OrderAlreadyCreated, retry.ResultCode);
  }

  /// <summary>Exercises update revision handling through the actual wire contract.</summary>
  [TestMethod]
  public async Task WhenUpdateIsRepeatedThenRevisionIncrementsOnlyOnceAsync()
  {
    await using var server = await IsolatedServer.StartAsync(GetContext());
    var request = TestData.Request();
    await server.CallAsync(ApiCalls.PlaceOrder(0, request));
    var update = ApiCalls.UpdateOrder(0, request with { TipCents = 250 }, 1);
    await server.CallAsync(update);
    var retry = await server.CallAsync(update);
    Assert.AreEqual(2UL, retry.CustomerOrder?.Revision);
  }

  /// <summary>Exercises cancel idempotency without touching any developer orders.</summary>
  [TestMethod]
  public async Task WhenCancellationIsRepeatedThenOrderStaysCancelledAsync()
  {
    await using var server = await IsolatedServer.StartAsync(GetContext());
    var request = TestData.Request();
    await server.CallAsync(ApiCalls.PlaceOrder(0, request));
    await server.CallAsync(ApiCalls.CancelOrder(0, request.OrderId));
    var retry = await server.CallAsync(ApiCalls.CancelOrder(0, request.OrderId));
    Assert.AreEqual(OrderStatus.Cancelled, retry.CustomerOrder?.Status);
  }

  /// <summary>Verifies automatic cooking transitions and simulated pickup.</summary>
  [TestMethod]
  public async Task WhenKitchenFinishesThenPickupMarksOrderDeliveredAsync()
  {
    await using var server = await IsolatedServer.StartAsync(GetContext(), fastKitchen: true);
    var request = TestData.Request();
    await server.CallAsync(ApiCalls.PlaceOrder(0, request));
    await server.WaitUntilReadyAsync(request.OrderId);
    var response = await server.CallAsync(ApiCalls.CompleteOrder(0, request.OrderId));
    Assert.AreEqual(OrderStatus.Delivered, response.CustomerOrder?.Status);
  }

  /// <summary>Exercises promo preview using the complete draft and customer ID.</summary>
  [TestMethod]
  public async Task WhenPromoIsPreviewedThenHypotheticalTotalsMatchAsync()
  {
    await using var server = await IsolatedServer.StartAsync(GetContext());
    var request = TestData.Request(5);
    var response = await server.CallAsync(ApiCalls.IsPromoValid(0, request, "SAVE5"));
    Assert.AreEqual(Pricing.Calculate(TestData.Menu(), request with { PromoCodes = ["SAVE5"] }), response.Totals);
  }
}

internal sealed class IsolatedServer : IAsyncDisposable
{
  private readonly Process process;
  private readonly PizzaService service;
  private readonly TestContext context;
  private readonly Task<string> output;
  private readonly Task<string> errors;
  private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

  private IsolatedServer(Process process, int port, TestContext context)
  {
    this.process = process;
    this.context = context;
    service = new(port: port, timeout: TimeSpan.FromSeconds(5));
    output = process.StandardOutput.ReadToEndAsync();
    errors = process.StandardError.ReadToEndAsync();
  }

  internal static async Task<IsolatedServer> StartAsync(TestContext context, bool fastKitchen = false)
  {
    string executable = Environment.GetEnvironmentVariable("PIZZA_SERVER_EXE") ?? Path.GetFullPath(
      Path.Combine(AppContext.BaseDirectory, "../../../../../WotCPizzaServer/x64/Debug/WotCPizzaServer.exe"));
    if (!File.Exists(executable)) Assert.Inconclusive("Build the neighboring server or set PIZZA_SERVER_EXE to run isolated integration tests.");
    string directory = Path.Combine(Path.GetTempPath(), "WotCPizzaClient.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    context.WriteLine("Isolated server data (retained for diagnostics): " + directory);
    var menu = JsonNode.Parse(TestData.Read<JsonElement>("menu.json").GetRawText()) ?? throw new InvalidDataException();
    var settings = menu["settings"] ?? throw new InvalidDataException();
    settings["startDelaySeconds"] = fastKitchen ? 1 : 3600;
    settings["timePerPizzaInSeconds"] = 1;
    await File.WriteAllTextAsync(Path.Combine(directory, "menu.json"), menu.ToJsonString());
    using var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    info.ArgumentList.Add("--port");
    info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    info.ArgumentList.Add("--data");
    info.ArgumentList.Add(directory);
    var process = Process.Start(info) ?? throw new InvalidOperationException("Server process did not start.");
    var server = new IsolatedServer(process, port, context);
    bool started = false;
    try
    {
      for (int attempt = 0; attempt < 50; attempt++)
      {
        server.deadline.Token.ThrowIfCancellationRequested();
        if (process.HasExited) throw new InvalidOperationException("Server exited during startup. Inspect captured output.");
        try
        {
          await server.CallAsync(ApiCalls.GetMenu());
          started = true;
          return server;
        }
        catch (SocketException) { await Task.Delay(100, server.deadline.Token); }
      }
      throw new TimeoutException("Server did not start listening.");
    }
    finally { if (!started) await server.DisposeAsync(); }
  }

  internal Task<ApiResponse> CallAsync(ApiRequest request) => service.SendAsync(PizzaService.Prepare(request), deadline.Token);

  internal async Task WaitUntilReadyAsync(string orderId)
  {
    while (true)
    {
      deadline.Token.ThrowIfCancellationRequested();
      var response = await CallAsync(ApiCalls.GetOrderStatus(0, orderId));
      if (response.Status == OrderStatus.ReadyForPickUp) return;
      if (response.ResultCode != ResultCode.Success) throw new InvalidDataException(response.Message);
      await Task.Delay(100, deadline.Token);
    }
  }

  public async ValueTask DisposeAsync()
  {
    deadline.Cancel();
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    context.WriteLine(await output);
    context.WriteLine(await errors);
    process.Dispose();
    deadline.Dispose();
  }
}
