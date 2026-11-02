using WotCPizzaClient.Models;
using WotCPizzaClient.Resources;

namespace WotCPizzaClient.ViewModels;

public sealed class OrderEntry(OrderRequest draft, CustomerOrder? snapshot = null) : ObservableObject
{
  public OrderRequest Draft { get; internal set; } = draft;
  public CustomerOrder? Snapshot { get; private set; } = snapshot;
  public ulong ExpectedRevision { get; internal set; } = snapshot?.Revision ?? 0;
  public bool Dirty { get; internal set; }
  public bool Conflict { get; internal set; }
  internal string? EditorTipMode { get; set; }
  internal string EditorPercent { get; set; } = "";
  internal string EditorDollars { get; set; } = "0";
  public string Label => $"{Draft.OrderId} — {Text.Get(Snapshot?.Status.ToString() ?? "New")}";

  /// <summary>Updates accepted state while retaining unsaved edits unless explicitly replaced.</summary>
  public void Accept(CustomerOrder order, bool replaceDraft = false)
  {
    ArgumentNullException.ThrowIfNull(order);

    if (order.OrderId != Draft.OrderId)
    {
      throw new ArgumentException(Text.Get("ProtocolFailure"), nameof(order));
    }

    Snapshot = order;

    if (Dirty && !replaceDraft)
    {
      Conflict |= ExpectedRevision != order.Revision;
    }
    else
    {
      Draft = order.ToRequest();
      ExpectedRevision = order.Revision;
      Dirty = false;
      Conflict = false;
      EditorTipMode = null;
    }

    OnPropertyChanged(nameof(Label));
  }
}

public sealed record PizzaLine(int Index, string Title, string Details);
