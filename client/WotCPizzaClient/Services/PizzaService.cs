using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WotCPizzaClient.Models;
using WotCPizzaClient.Resources;

namespace WotCPizzaClient.Services;

public interface IPizzaService
{
  /// <summary>Sends the exact prepared JSON request and reads one complete response.</summary>
  Task<ApiResponse> SendAsync(string json, CancellationToken cancellationToken);
}

public sealed class PizzaService(string host = "127.0.0.1", int port = 5050, TimeSpan? timeout = null) : IPizzaService
{
  public const int MaxRequestBytes = 1024 * 1024;
  public const int MaxResponseBytes = 16 * 1024 * 1024;

  /// <summary>Freezes a request before the first attempt so retries cannot change its contents.</summary>
  public static string Prepare(ApiRequest request)
  {
    ArgumentNullException.ThrowIfNull(request);
    string json = JsonSerializer.Serialize(request, WireJson.Options);
    if (Encoding.UTF8.GetByteCount(json) + 1 > MaxRequestBytes) throw new OrderValidationException(Text.Get("FrameLimit"));
    return json;
  }

  /// <summary>Connects, sends one LF-terminated message, and assembles a bounded response through LF.</summary>
  public async Task<ApiResponse> SendAsync(string json, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException(Text.Get("ProtocolFailure"), nameof(json));
    byte[] request = Encoding.UTF8.GetBytes(json + "\n");
    if (request.Length > MaxRequestBytes || json.Contains('\n')) throw new OrderValidationException(Text.Get("FrameLimit"));
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
    using var client = new TcpClient();
    try
    {
      await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
      await using var stream = client.GetStream();
      await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
      using var response = new MemoryStream();
      byte[] buffer = new byte[8192];
      while (true)
      {
        int count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
        if (count == 0) throw new IOException(Text.Get("IncompleteResponse"));
        int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
        int length = newline < 0 ? count : newline;
        if (response.Length + length > MaxResponseBytes) throw new InvalidDataException(Text.Get("FrameLimit"));
        response.Write(buffer, 0, length);
        if (newline < 0) continue;
        for (int i = newline + 1; i < count; i++)
          if (buffer[i] is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
            throw new InvalidDataException(Text.Get("ProtocolFailure"));
        try
        {
          var result = JsonSerializer.Deserialize<ApiResponse>(response.GetBuffer().AsSpan(0, (int)response.Length), WireJson.Options);
          if (result is null || !Enum.IsDefined(result.ResultCode)) throw new InvalidDataException(Text.Get("ProtocolFailure"));
          return result;
        }
        catch (JsonException error) { throw new InvalidDataException(Text.Get("ProtocolFailure"), error); }
      }
    }
    catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
    {
      throw new TimeoutException(Text.Get("Timeout"), error);
    }
  }
}

public static class ApiCalls
{
  /// <summary>Requests the current menu.</summary>
  public static ApiRequest GetMenu() => new(FunctionId.GetMenu);
  /// <summary>Requests a full accepted order.</summary>
  public static ApiRequest GetOrder(uint customerId, string orderId) => new(FunctionId.GetOrder, new(customerId, orderId));
  /// <summary>Requests all orders for an anonymous customer.</summary>
  public static ApiRequest GetAllOrders(uint customerId) => new(FunctionId.GetAllOrders, new(customerId));
  /// <summary>Places a client-identified order.</summary>
  public static ApiRequest PlaceOrder(uint customerId, OrderRequest order) => new(FunctionId.PlaceOrder, new(customerId, CustomerOrder: order));
  /// <summary>Requests a lightweight lifecycle snapshot.</summary>
  public static ApiRequest GetOrderStatus(uint customerId, string orderId) => new(FunctionId.GetOrderStatus, new(customerId, orderId));
  /// <summary>Updates an order using optimistic concurrency.</summary>
  public static ApiRequest UpdateOrder(uint customerId, OrderRequest order, ulong revision) => new(FunctionId.UpdateOrder, new(customerId, CustomerOrder: order, ExpectedRevision: revision));
  /// <summary>Cancels a pending order.</summary>
  public static ApiRequest CancelOrder(uint customerId, string orderId) => new(FunctionId.CancelOrder, new(customerId, orderId));
  /// <summary>Validates a promo against the entire draft.</summary>
  public static ApiRequest IsPromoValid(uint customerId, OrderRequest order, string code) => new(FunctionId.IsPromoValid, new(customerId, CustomerOrder: order, PromoCode: code));
  /// <summary>Simulates restaurant-side pickup.</summary>
  public static ApiRequest CompleteOrder(uint customerId, string orderId) => new(FunctionId.CompleteOrder, new(customerId, orderId));
}
