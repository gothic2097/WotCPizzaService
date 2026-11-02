using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WotCPizzaClient.Models;
using WotCPizzaClient.Services;

namespace WotCPizzaClient.Tests;

[TestClass]
public class PizzaServiceTests
{
  /// <summary>Assembles a UTF-8 response even when multibyte characters cross read boundaries.</summary>
  [TestMethod]
  public async Task WhenResponseIsFragmentedThenEntireJsonIsReadAsync()
  {
    var result = await ExchangeAsync(async (stream, token) =>
    {
      var bytes = Encoding.UTF8.GetBytes("{\"resultCode\":-1,\"message\":\"caf\u00e9\"}\n");

      foreach (byte value in bytes)
      {
        await stream.WriteAsync(new byte[] { value }, token);
      }
    });
    Assert.AreEqual("caf\u00e9", result.Message);
  }

  /// <summary>EOF is not success until the frame terminator is present.</summary>
  [TestMethod]
  public async Task WhenConnectionClosesBeforeNewlineThenResponseIsRejectedAsync()
  {
    await Assert.ThrowsExceptionAsync<IOException>(() => ExchangeAsync(async (stream, token) =>
      await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"resultCode\":0}"), token)));
  }

  /// <summary>Missing required result codes cannot default to success.</summary>
  [TestMethod]
  public async Task WhenResultCodeIsMissingThenResponseIsRejectedAsync()
  {
    await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ExchangeAsync(async (stream, token) =>
      await stream.WriteAsync(Encoding.UTF8.GetBytes("{}\n"), token)));
  }

  /// <summary>Deadlines cancel pending reads rather than leaving background I/O running.</summary>
  [TestMethod]
  public async Task WhenPeerNeverRespondsThenRequestTimesOutAsync()
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var client = new PizzaService(port: ((IPEndPoint)listener.LocalEndpoint).Port, timeout: TimeSpan.FromMilliseconds(100));
    await Assert.ThrowsExceptionAsync<TimeoutException>(() => client.SendAsync(PizzaService.Prepare(ApiCalls.GetMenu()), CancellationToken.None));
  }

  /// <summary>Explicit caller cancellation is distinct from a timeout.</summary>
  [TestMethod]
  public async Task WhenCallerCancelsThenOperationIsCancelledAsync()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var client = new PizzaService();
    await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.SendAsync(PizzaService.Prepare(ApiCalls.GetMenu()), cancellation.Token));
  }

  /// <summary>Frozen requests are unaffected by later mutation of source arrays.</summary>
  [TestMethod]
  public void WhenSourceDraftChangesThenPreparedJsonIsUnchanged()
  {
    var request = TestData.Request();
    var prepared = PizzaService.Prepare(ApiCalls.PlaceOrder(0, request));
    request.Pizzas[0] = new(2, 99, []);
    StringAssert.Contains(prepared, "\"count\":1");
  }

  /// <summary>Oversized requests fail locally before opening a connection.</summary>
  [TestMethod]
  public void WhenRequestExceedsLimitThenPreparationRejectsIt()
  {
    var request = TestData.Request() with { PromoCodes = [new string('A', PizzaService.MaxRequestBytes)] };
    Assert.ThrowsException<OrderValidationException>(() => PizzaService.Prepare(ApiCalls.PlaceOrder(0, request)));
  }

  /// <summary>Function IDs remain compatible with the C++ contract.</summary>
  [TestMethod]
  public void WhenUpdateIsPreparedThenEnvelopeUsesExactWireNames()
  {
    var prepared = PizzaService.Prepare(ApiCalls.UpdateOrder(42, TestData.Request(), 3));
    StringAssert.Contains(prepared, "\"FunctionId\":5,\"Input\":{\"customerId\":42,");
  }

  private static async Task<ApiResponse> ExchangeAsync(Func<NetworkStream, CancellationToken, Task> respond)
  {
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var client = new PizzaService(port: ((IPEndPoint)listener.LocalEndpoint).Port);
    Task peer = ServeAsync(listener, respond, deadline.Token);

    try
    {
      return await client.SendAsync(PizzaService.Prepare(ApiCalls.GetMenu()), deadline.Token);
    }
    finally
    {
      await peer;
    }
  }

  private static async Task ServeAsync(TcpListener listener, Func<NetworkStream, CancellationToken, Task> respond, CancellationToken token)
  {
    using var peer = await listener.AcceptTcpClientAsync(token);
    await using var stream = peer.GetStream();
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
    await reader.ReadLineAsync(token);
    await respond(stream, token);
  }
}
