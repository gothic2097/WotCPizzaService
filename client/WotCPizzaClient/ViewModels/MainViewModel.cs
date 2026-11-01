using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using WotCPizzaClient.Models;
using WotCPizzaClient.Resources;
using WotCPizzaClient.Services;

namespace WotCPizzaClient.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
  private readonly IPizzaService service;
  private readonly IClientDialogs dialogs;
  private readonly CancellationTokenSource lifetime = new();
  private Menu? menu;
  private uint customerId;
  private string customerIdText = "0";
  private string refreshMinutes = "5";
  private bool signedIn;
  private bool busy;
  private bool showSpinner;
  private bool unresolvedMutation;
  private bool loading;
  private OrderEntry? selectedOrder;
  private Totals? totals;
  private string validation = "";
  private string notice = Text.Get("SignInHint");
  private string promoText = "";
  private string tipMode = "Dollars";
  private string customPercent = "";
  private string customDollars = "0";
  private bool priceMismatch;
  private bool refreshAfterSubmission;

  /// <summary>Creates the workflow with injectable network and dialog dependencies.</summary>
  public MainViewModel(IPizzaService service, IClientDialogs dialogs)
  {
    this.service = service;
    this.dialogs = dialogs;
    SignInCommand = new(_ => RunAsync(SignInAsync), _ => !IsBusy && !IsSignedIn);
    RefreshCommand = new(_ => RunAsync(RefreshOrdersAsync), _ => CanInteract);
    NewOrderCommand = new(_ => RunLocal(StartNewOrder), _ => CanInteract);
    AddPizzaCommand = new(_ => RunLocal(() => EditPizza(null)), _ => CanEdit);
    EditPizzaCommand = new(p => RunLocal(() => EditPizza(p as PizzaLine)), _ => CanEdit);
    RemovePizzaCommand = new(p => RunLocal(() => RemovePizza(p as PizzaLine)), _ => CanEdit);
    ApplyPromoCommand = new(_ => RunAsync(ApplyPromoAsync), _ => CanEdit && totals is not null && !string.IsNullOrWhiteSpace(PromoText));
    RemovePromoCommand = new(p => RunLocal(() => RemovePromo(p as string)), _ => CanEdit);
    SubmitCommand = new(_ => RunAsync(SubmitAsync), _ => CanEdit && totals is not null && !priceMismatch && selectedOrder is { Conflict: false });
    CancelCommand = new(_ => RunAsync(CancelAsync), _ => CanInteract && selectedOrder?.Snapshot?.Status == OrderStatus.NotStarted);
    PickupCommand = new(_ => RunAsync(PickupAsync), _ => CanInteract && selectedOrder?.Snapshot?.Status == OrderStatus.ReadyForPickUp);
    ReceiptCommand = new(_ => RunLocal(ShowReceipt), _ => CanInteract && selectedOrder is not null && (selectedOrder.Snapshot is not null || totals is not null));
    RepriceCommand = new(_ => RunLocal(Reprice), _ => CanInteract && IsHistorical && selectedOrder?.Snapshot?.Status == OrderStatus.NotStarted);
    ReconcileCommand = new(_ => RunLocal(Reconcile), _ => CanInteract && selectedOrder is { Conflict: true });
  }

  public ObservableCollection<OrderEntry> Orders { get; } = [];
  public ObservableCollection<PizzaLine> Lines { get; } = [];
  public ObservableCollection<string> PromoCodes { get; } = [];
  public AsyncCommand SignInCommand { get; }
  public AsyncCommand RefreshCommand { get; }
  public RelayCommand NewOrderCommand { get; }
  public RelayCommand AddPizzaCommand { get; }
  public RelayCommand EditPizzaCommand { get; }
  public RelayCommand RemovePizzaCommand { get; }
  public AsyncCommand ApplyPromoCommand { get; }
  public RelayCommand RemovePromoCommand { get; }
  public AsyncCommand SubmitCommand { get; }
  public AsyncCommand CancelCommand { get; }
  public AsyncCommand PickupCommand { get; }
  public RelayCommand ReceiptCommand { get; }
  public RelayCommand RepriceCommand { get; }
  public RelayCommand ReconcileCommand { get; }
  public event EventHandler? RefreshTimerReset;
  public string CustomerIdText { get => customerIdText; set => Set(ref customerIdText, value); }
  public string RefreshMinutes
  {
    get => refreshMinutes;
    set { if (Set(ref refreshMinutes, value)) { OnPropertyChanged(nameof(RefreshError)); RefreshTimerReset?.Invoke(this, EventArgs.Empty); } }
  }
  public string RefreshError => RefreshInterval is null ? Text.Get("RefreshInvalid") : "";
  public TimeSpan? RefreshInterval => int.TryParse(RefreshMinutes, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) && minutes is >= 1 and <= 1440 ? TimeSpan.FromMinutes(minutes) : null;
  /// <summary>Uses a short post-submission delay until the next completed refresh.</summary>
  public TimeSpan? NextRefreshInterval => refreshAfterSubmission ? TimeSpan.FromSeconds(15) : RefreshInterval;
  public bool IsSignedIn => signedIn;
  public bool IsBusy => busy;
  public bool CanSignIn => !signedIn && !busy;
  public bool CanInteract => signedIn && !busy;
  public bool ShowSpinner { get => showSpinner; private set => Set(ref showSpinner, value); }
  public bool HasUnresolvedMutation => unresolvedMutation;
  public OrderEntry? SelectedOrder => selectedOrder;
  public bool HasOrder => selectedOrder is not null;
  public bool CanEdit => CanInteract && selectedOrder is not null && !selectedOrder.Conflict && !IsHistorical &&
    (selectedOrder.Snapshot is null || selectedOrder.Snapshot.Status == OrderStatus.NotStarted);
  public bool IsHistorical => selectedOrder is not null && menu is not null && selectedOrder.Draft.MenuVersion != menu.MenuVersion;
  public bool HasConflict => selectedOrder?.Conflict == true;
  public bool CanCancel => selectedOrder?.Snapshot?.Status == OrderStatus.NotStarted;
  public bool CanPickup => selectedOrder?.Snapshot?.Status == OrderStatus.ReadyForPickUp;
  public string HistoricalReceipt => IsHistorical ? selectedOrder?.Snapshot?.Description ?? "" : "";
  public string SubmitLabel => Text.Get(selectedOrder?.Snapshot is null ? "Place" : "Update");
  public string ReceiptLabel => Text.Get(selectedOrder?.Snapshot is null ? "Description" : "Receipt");
  public string Notice { get => notice; private set => Set(ref notice, value); }
  public string Validation { get => validation; private set => Set(ref validation, value); }
  public string TimeDescription => selectedOrder?.Snapshot is { } order ? order.Status switch
  {
    OrderStatus.NotStarted => Text.Get("PendingTime", order.TimeTillReady),
    OrderStatus.Started => Text.Get("RemainingTime", order.TimeTillReady),
    _ => ""
  } : "";
  public string Subtotal => Amount(totals?.SubtotalCents);
  public string Discount => Amount(totals?.DiscountCents);
  public string BeforeTax => Amount(totals?.TotalBeforeTaxAndTipCents);
  public string Tax => Amount(totals?.TaxCents);
  public string Tip => Amount(totals?.TipCents);
  public string Total => Amount(totals?.TotalAfterTaxAndTipCents);
  public string PromoText { get => promoText; set { if (Set(ref promoText, value)) ApplyPromoCommand.Refresh(); } }
  public bool Tip15 { get => tipMode == "15"; set { if (value) ChangeTipMode("15"); } }
  public bool Tip18 { get => tipMode == "18"; set { if (value) ChangeTipMode("18"); } }
  public bool Tip20 { get => tipMode == "20"; set { if (value) ChangeTipMode("20"); } }
  public bool TipPercent { get => tipMode == "Percent"; set { if (value) ChangeTipMode("Percent"); } }
  public bool TipDollars { get => tipMode == "Dollars"; set { if (value) ChangeTipMode("Dollars"); } }
  public string CustomPercent { get => customPercent; set { if (Set(ref customPercent, value) && !loading && TipPercent) DraftChanged(); } }
  public string CustomDollars { get => customDollars; set { if (Set(ref customDollars, value) && !loading && TipDollars) DraftChanged(); } }

  /// <summary>Selects an order, fetching accepted state without replacing unsaved edits.</summary>
  public async Task SelectOrderAsync(OrderEntry? entry)
  {
    if (busy || entry is null || selectedOrder == entry) return;
    selectedOrder = entry;
    LoadDraft();
    if (entry.Snapshot is not null)
      await RunAsync(async () =>
      {
        var response = await SendAsync(ApiCalls.GetOrder(customerId, entry.Draft.OrderId));
        if (response.ResultCode == ResultCode.Success) ApplySnapshot(entry, RequiredOrder(response));
        else await HandleErrorAsync(response);
      });
    RefreshTimerReset?.Invoke(this, EventArgs.Empty);
  }

  /// <summary>Refreshes eligible orders when the UI is idle.</summary>
  public async Task RefreshAsync()
  {
    if (CanInteract) await RefreshCommand.ExecuteAsync();
  }

  /// <summary>Warns before losing uncertain requests or unsaved edits.</summary>
  public bool ConfirmClose() => unresolvedMutation
    ? dialogs.Confirm(Text.Get("CloseUncertain"))
    : !Orders.Any(o => o.Dirty) || dialogs.Confirm(Text.Get("CloseDirty"));

  /// <summary>Cancels active I/O when the window closes.</summary>
  public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }

  private async Task SignInAsync()
  {
    if (!uint.TryParse(CustomerIdText, NumberStyles.None, CultureInfo.InvariantCulture, out customerId))
      throw new OrderValidationException(Text.Get("CustomerInvalid"));
    var response = await SendAsync(ApiCalls.GetMenu());
    if (response.ResultCode != ResultCode.Success) { await HandleErrorAsync(response); return; }
    menu = response.Menu ?? throw ProtocolError();
    Pricing.ValidateMenu(menu);
    response = await SendAsync(ApiCalls.GetAllOrders(customerId));
    if (response.ResultCode != ResultCode.Success && !(response.ResultCode == ResultCode.Fail && response.Error == "not_found"))
    { await HandleErrorAsync(response); return; }
    Orders.Clear();
    foreach (var order in response.CustomerOrders ?? []) Orders.Add(new(order.ToRequest(), order));
    signedIn = true;
    Notice = Text.Get("ChooseOrder");
    RefreshTimerReset?.Invoke(this, EventArgs.Empty);
  }

  private void StartNewOrder()
  {
    if (menu is null) return;
    var previous = Orders.FirstOrDefault(o => o.Snapshot is null);
    if (previous is not null)
    {
      if (!dialogs.Confirm(Text.Get("ReplaceDraft"))) return;
      Orders.Remove(previous);
    }
    selectedOrder = new(new OrderRequest
    {
      OrderId = Guid.NewGuid().ToString("D"), MenuVersion = menu.MenuVersion,
      Pizzas = [], PromoCodes = []
    });
    Orders.Add(selectedOrder);
    LoadDraft();
  }

  private void EditPizza(PizzaLine? line)
  {
    if (menu is null || selectedOrder is null) return;
    var result = dialogs.EditPizza(menu, line is null ? null : selectedOrder.Draft.Pizzas[line.Index]);
    if (result is null) return;
    var pizzas = selectedOrder.Draft.Pizzas.ToList();
    if (line is null) pizzas.Add(result); else pizzas[line.Index] = result;
    if (pizzas.Count > 100 || pizzas.Sum(p => (long)p.Count) > 1000) throw new OrderValidationException(Text.Get("LinesInvalid"));
    selectedOrder.Draft = selectedOrder.Draft with { Pizzas = [.. pizzas] };
    DraftChanged();
  }

  private void RemovePizza(PizzaLine? line)
  {
    if (selectedOrder is null || line is null) return;
    selectedOrder.Draft = selectedOrder.Draft with { Pizzas = selectedOrder.Draft.Pizzas.Where((_, i) => i != line.Index).ToArray() };
    DraftChanged();
  }

  private void RemovePromo(string? code)
  {
    if (selectedOrder is null || code is null) return;
    selectedOrder.Draft = selectedOrder.Draft with { PromoCodes = selectedOrder.Draft.PromoCodes.Where(c => c != code).ToArray() };
    DraftChanged();
  }

  private async Task ApplyPromoAsync()
  {
    if (selectedOrder is null || menu is null) return;
    string code = PromoText.Trim();
    if (code.Length == 0) throw new OrderValidationException(Text.Get("PromoEmpty"));
    if (selectedOrder.Draft.PromoCodes.Contains(code, StringComparer.Ordinal))
      throw new OrderValidationException(Text.Get("PromoDuplicate", code));
    var candidate = selectedOrder.Draft with { PromoCodes = [.. selectedOrder.Draft.PromoCodes, code] };
    var response = await SendAsync(ApiCalls.IsPromoValid(customerId, selectedOrder.Draft, code));
    if (response.ResultCode != ResultCode.PromoValid) { await HandleErrorAsync(response); return; }
    var quote = Pricing.Calculate(menu, candidate);
    if (quote != response.Totals)
    {
      priceMismatch = true;
      Validation = Text.Get("UnconfirmedPrice");
      return;
    }
    selectedOrder.Draft = candidate;
    PromoText = "";
    DraftChanged();
  }

  private async Task SubmitAsync()
  {
    if (selectedOrder is null || menu is null) return;
    Pricing.Calculate(menu, selectedOrder.Draft);
    bool placement = selectedOrder.Snapshot is null;
    var response = await SendAsync(placement ? ApiCalls.PlaceOrder(customerId, selectedOrder.Draft)
      : ApiCalls.UpdateOrder(customerId, selectedOrder.Draft, selectedOrder.ExpectedRevision));
    if (response.ResultCode is ResultCode.Success or ResultCode.OrderAlreadyCreated)
    {
      ApplySnapshot(selectedOrder, RequiredOrder(response), true);
      dialogs.Show(Text.Get(placement ? "Created" : "Updated"));
      refreshAfterSubmission = true;
      RefreshTimerReset?.Invoke(this, EventArgs.Empty);
    }
    else await HandleErrorAsync(response);
  }

  private async Task CancelAsync()
  {
    if (selectedOrder is null || !dialogs.Confirm(Text.Get("CancelConfirm"))) return;
    var response = await SendAsync(ApiCalls.CancelOrder(customerId, selectedOrder.Draft.OrderId));
    if (response.ResultCode == ResultCode.Success) ApplySnapshot(selectedOrder, RequiredOrder(response), true);
    else await HandleErrorAsync(response);
  }

  private async Task PickupAsync()
  {
    if (selectedOrder is null) return;
    var response = await SendAsync(ApiCalls.CompleteOrder(customerId, selectedOrder.Draft.OrderId));
    if (response.ResultCode == ResultCode.Success) ApplySnapshot(selectedOrder, RequiredOrder(response), true);
    else await HandleErrorAsync(response);
  }

  private async Task RefreshOrdersAsync()
  {
    foreach (var entry in Orders.Where(o => o.Snapshot is not null && o.Snapshot.Status is not (OrderStatus.Delivered or OrderStatus.Cancelled)).ToArray())
    {
      lifetime.Token.ThrowIfCancellationRequested();
      var response = await SendAsync(ApiCalls.GetOrderStatus(customerId, entry.Draft.OrderId));
      if (response.ResultCode != ResultCode.Success) { await HandleErrorAsync(response); continue; }
      if (response.Revision != entry.Snapshot?.Revision)
      {
        response = await SendAsync(ApiCalls.GetOrder(customerId, entry.Draft.OrderId));
        if (response.ResultCode == ResultCode.Success) ApplySnapshot(entry, RequiredOrder(response));
        else await HandleErrorAsync(response);
      }
      else if (entry.Snapshot is { } snapshot && response.TimeTillReady is { } seconds)
        ApplySnapshot(entry, snapshot with { TimeTillReady = seconds });
    }
    refreshAfterSubmission = false;
    RefreshTimerReset?.Invoke(this, EventArgs.Empty);
  }

  private async Task HandleErrorAsync(ApiResponse response)
  {
    if (response.Error == "stale_menu")
    {
      var refreshed = await SendAsync(ApiCalls.GetMenu());
      if (refreshed.ResultCode != ResultCode.Success) { dialogs.Show(ServerMessage(refreshed)); return; }
      menu = refreshed.Menu ?? throw ProtocolError();
      Pricing.ValidateMenu(menu);
      if (selectedOrder is not null)
      {
        selectedOrder.Draft = selectedOrder.Draft with { MenuVersion = menu.MenuVersion };
        selectedOrder.Dirty = true;
        Recalculate();
      }
      dialogs.Show(Text.Get("StaleMenu"));
    }
    else if (response.Error == "revision_conflict" && selectedOrder is not null)
    {
      var refreshed = await SendAsync(ApiCalls.GetOrder(customerId, selectedOrder.Draft.OrderId));
      if (refreshed.ResultCode == ResultCode.Success) ApplySnapshot(selectedOrder, RequiredOrder(refreshed));
      else dialogs.Show(ServerMessage(refreshed));
      dialogs.Show(Text.Get("ChangedOnServer"));
    }
    else if (response.ResultCode == ResultCode.OrderAlreadyStarted && selectedOrder is not null)
    {
      ApplySnapshot(selectedOrder, RequiredOrder(response), true);
      dialogs.Show(Text.Get("KitchenWon"));
    }
    else dialogs.Show(ServerMessage(response));
  }

  private void ApplySnapshot(OrderEntry entry, CustomerOrder snapshot, bool replaceDraft = false)
  {
    bool retained = entry.Dirty && !replaceDraft;
    entry.Accept(snapshot, replaceDraft);
    if (entry == selectedOrder)
    {
      if (retained) Recalculate(); else LoadDraft();
    }
  }

  private void Reprice()
  {
    if (selectedOrder is null || menu is null || !dialogs.Confirm(Text.Get("RepriceConfirm"))) return;
    selectedOrder.Draft = selectedOrder.Draft with { MenuVersion = menu.MenuVersion };
    DraftChanged();
  }

  private void Reconcile()
  {
    if (selectedOrder?.Snapshot is not { } snapshot) return;
    if (snapshot.Status == OrderStatus.NotStarted && dialogs.Confirm(Text.Get("ReconcileConfirm")))
    {
      selectedOrder.ExpectedRevision = snapshot.Revision;
      selectedOrder.Conflict = false;
      Recalculate();
    }
    else
    {
      if (snapshot.Status != OrderStatus.NotStarted && !dialogs.Confirm(Text.Get("DiscardConfirm"))) return;
      ApplySnapshot(selectedOrder, snapshot, true);
    }
  }

  private void ShowReceipt()
  {
    if (selectedOrder is null || menu is null) return;
    dialogs.ShowReceipt(selectedOrder.Snapshot?.Description ?? Pricing.Describe(menu, selectedOrder.Draft));
  }

  private void ChangeTipMode(string mode)
  {
    if (loading || tipMode == mode) return;
    tipMode = mode;
    if (mode != "Dollars") customDollars = "";
    if (mode != "Percent") customPercent = "";
    NotifyTip();
    DraftChanged();
  }

  private void DraftChanged()
  {
    if (selectedOrder is null || loading) return;
    selectedOrder.Dirty = true;
    selectedOrder.EditorTipMode = tipMode;
    selectedOrder.EditorPercent = customPercent;
    selectedOrder.EditorDollars = customDollars;
    priceMismatch = false;
    Recalculate();
  }

  private void LoadDraft()
  {
    loading = true;
    if (selectedOrder is { } entry)
    {
      var draft = entry.Draft;
      tipMode = draft.TipBasisPoints switch { 1500 => "15", 1800 => "18", 2000 => "20", > 0 => "Percent", _ => "Dollars" };
      customPercent = tipMode == "Percent" ? (draft.TipBasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture) : "";
      customDollars = tipMode == "Dollars" ? (draft.TipCents / 100m).ToString("0.00", CultureInfo.InvariantCulture) : "";
      if (entry.EditorTipMode is { } savedMode)
      {
        tipMode = savedMode;
        customPercent = entry.EditorPercent;
        customDollars = entry.EditorDollars;
      }
    }
    priceMismatch = false;
    loading = false;
    NotifyTip();
    Recalculate();
  }

  private void Recalculate()
  {
    totals = null;
    Validation = "";
    Lines.Clear();
    PromoCodes.Clear();
    if (selectedOrder is null || menu is null) { RefreshState(); return; }
    foreach (var code in selectedOrder.Draft.PromoCodes) PromoCodes.Add(code);
    try
    {
      if (selectedOrder.Dirty || selectedOrder.Snapshot is null)
      {
        long cents = tipMode == "Dollars" ? Pricing.ParseTip(CustomDollars, false) : 0;
        long basisPoints = tipMode switch
        {
          "15" => 1500, "18" => 1800, "20" => 2000,
          "Percent" => Pricing.ParseTip(CustomPercent, true), _ => 0
        };
        selectedOrder.Draft = selectedOrder.Draft with { TipCents = cents, TipBasisPoints = basisPoints };
        totals = Pricing.Calculate(menu, selectedOrder.Draft);
      }
      else totals = selectedOrder.Snapshot.Totals;
    }
    catch (OrderValidationException error) { Validation = error.Message; }
    if (!IsHistorical)
      for (int i = 0; i < selectedOrder.Draft.Pizzas.Length; i++)
      {
        var pizza = selectedOrder.Draft.Pizzas[i];
        try
        {
          long unit = Pricing.UnitPrice(menu, pizza);
          var size = menu.Pizzas.Single(p => p.Code == pizza.PizzaCode);
          List<string> toppings = [];
          foreach (var selection in pizza.Toppings)
          {
            var category = menu.Toppings.Single(t => t.Code == selection.ToppingBaseCode);
            foreach (var code in selection.Toppings)
              toppings.Add(Text.Get("ToppingLine", category.Toppings.Single(t => t.Code == code).Name,
                Pricing.Dollars(size.CostCents * category.CostBasisPoints / Pricing.RateScale)));
          }
          Lines.Add(new(i, Text.Get("PizzaLine", pizza.Count, size.Name, Pricing.Dollars(unit), Pricing.Dollars(unit * pizza.Count)),
            Text.Get("BaseLine", Pricing.Dollars(size.CostCents)) + "\n" + (toppings.Count == 0 ? Text.Get("NoToppings") : string.Join("\n", toppings))));
        }
        catch (OrderValidationException error) { Lines.Add(new(i, $"#{pizza.PizzaCode} × {pizza.Count}", error.Message)); }
      }
    Notice = selectedOrder.Conflict ? Text.Get("ChangedOnServer") : IsHistorical ? Text.Get("Historical") : selectedOrder.Dirty ? Text.Get("Unsaved") : "";
    if (priceMismatch) Validation = Text.Get("UnconfirmedPrice");
    RefreshState();
  }

  private async Task<ApiResponse> SendAsync(ApiRequest request)
  {
    string json = PizzaService.Prepare(request);
    bool mutation = request.FunctionId is FunctionId.PlaceOrder or FunctionId.UpdateOrder or FunctionId.CancelOrder or FunctionId.CompleteOrder;
    unresolvedMutation = mutation;
    while (true)
    {
      lifetime.Token.ThrowIfCancellationRequested();
      try
      {
        var response = await service.SendAsync(json, lifetime.Token);
        if (response.ResultCode == ResultCode.ConnectionError) throw new IOException(ServerMessage(response));
        ValidateResponse(request, response);
        unresolvedMutation = false;
        return response;
      }
      catch (InvalidDataException error) { RetryOrExit(error.Message); }
      catch (IOException error) { RetryOrExit(error.Message); }
      catch (SocketException error) { RetryOrExit(error.Message); }
      catch (TimeoutException error) { RetryOrExit(error.Message); }
    }
  }

  private void RetryOrExit(string message)
  {
    if (lifetime.IsCancellationRequested) throw new OperationCanceledException();
    if (dialogs.Retry(message)) return;
    lifetime.Cancel();
    dialogs.Exit();
    throw new OperationCanceledException();
  }

  private static void ValidateResponse(ApiRequest request, ApiResponse response)
  {
    if (response.CustomerOrder is { } order)
    {
      ValidateOrder(order);
      if (order.CustomerId != request.Input?.CustomerId || order.OrderId != (request.Input?.OrderId ?? request.Input?.CustomerOrder?.OrderId)) throw ProtocolError();
    }
    if (response.ResultCode == ResultCode.OrderAlreadyStarted && response.CustomerOrder is null) throw ProtocolError();
    if (response.ResultCode == ResultCode.PromoValid && response.Totals is null) throw ProtocolError();
    if (response.ResultCode is not (ResultCode.Success or ResultCode.OrderAlreadyCreated)) return;
    switch (request.FunctionId)
    {
      case FunctionId.GetMenu:
        if (response.Menu is null) throw ProtocolError();
        break;
      case FunctionId.GetAllOrders:
        if (response.CustomerOrders is null) throw ProtocolError();
        foreach (var item in response.CustomerOrders)
        {
          ValidateOrder(item);
          if (item.CustomerId != request.Input?.CustomerId) throw ProtocolError();
        }
        if (response.CustomerOrders.Select(o => o.OrderId).Distinct(StringComparer.Ordinal).Count() != response.CustomerOrders.Length) throw ProtocolError();
        break;
      case FunctionId.GetOrderStatus:
        if (response.Status is null || !Enum.IsDefined(response.Status.Value) || response.Revision is null or 0 || response.TimeTillReady is null) throw ProtocolError();
        break;
      case FunctionId.IsPromoValid:
        throw ProtocolError();
      default:
        if (response.CustomerOrder is null) throw ProtocolError();
        break;
    }
  }

  private static void ValidateOrder(CustomerOrder order)
  {
    if (order is null || !Guid.TryParseExact(order.OrderId, "D", out _) || string.IsNullOrWhiteSpace(order.MenuVersion) ||
        order.Pizzas is null || order.Pizzas.Length is < 1 or > 100 || order.PromoCodes is null || order.Totals is null ||
        order.Revision == 0 || !Enum.IsDefined(order.Status) || string.IsNullOrWhiteSpace(order.Description) ||
        order.Pizzas.Any(p => p is null || p.Count is < 1 or > 100 || p.Toppings is null || p.Toppings.Any(t => t is null || t.Toppings is null))) throw ProtocolError();
    var t = order.Totals;
    if (new[] { t.SubtotalCents, t.DiscountCents, t.TotalBeforeTaxAndTipCents, t.TaxCents, t.TipCents, t.TotalAfterTaxAndTipCents }.Any(c => c is < 0 or > Pricing.MaxMoney) ||
        t.SubtotalCents - t.DiscountCents != t.TotalBeforeTaxAndTipCents || t.TotalBeforeTaxAndTipCents + t.TaxCents + t.TipCents != t.TotalAfterTaxAndTipCents) throw ProtocolError();
  }

  private async Task RunAsync(Func<Task> action)
  {
    if (busy) return;
    busy = true;
    RefreshState();
    using var spinnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    Task spinner = DelaySpinnerAsync(spinnerCancellation.Token);
    try { await action(); }
    catch (OrderValidationException error) { Validation = error.Message; dialogs.Show(error.Message); }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    finally
    {
      spinnerCancellation.Cancel();
      await spinner;
      ShowSpinner = false;
      busy = false;
      RefreshState();
    }
  }

  private void RunLocal(Action action)
  {
    if (busy) return;
    busy = true;
    RefreshState();
    try { action(); }
    catch (OrderValidationException error) { Validation = error.Message; dialogs.Show(error.Message); }
    finally { busy = false; RefreshState(); }
  }

  private async Task DelaySpinnerAsync(CancellationToken cancellationToken)
  {
    try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); ShowSpinner = true; }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* Cancellation hides a short-lived operation's spinner. */ }
  }

  private void NotifyTip()
  {
    foreach (string name in new[] { nameof(Tip15), nameof(Tip18), nameof(Tip20), nameof(TipPercent), nameof(TipDollars), nameof(CustomPercent), nameof(CustomDollars) }) OnPropertyChanged(name);
  }

  private void RefreshState()
  {
    foreach (string name in new[] { nameof(IsSignedIn), nameof(IsBusy), nameof(CanSignIn), nameof(CanInteract), nameof(CanEdit), nameof(SelectedOrder), nameof(HasOrder),
      nameof(IsHistorical), nameof(HasConflict), nameof(CanCancel), nameof(CanPickup), nameof(HistoricalReceipt), nameof(SubmitLabel), nameof(ReceiptLabel), nameof(TimeDescription),
      nameof(Subtotal), nameof(Discount), nameof(BeforeTax), nameof(Tax), nameof(Tip), nameof(Total) }) OnPropertyChanged(name);
    foreach (var command in new[] { SignInCommand, RefreshCommand, ApplyPromoCommand, SubmitCommand, CancelCommand, PickupCommand }) command.Refresh();
    foreach (var command in new[] { NewOrderCommand, AddPizzaCommand, EditPizzaCommand, RemovePizzaCommand, RemovePromoCommand, ReceiptCommand, RepriceCommand, ReconcileCommand }) command.Refresh();
  }

  private static CustomerOrder RequiredOrder(ApiResponse response) => response.CustomerOrder ?? throw ProtocolError();
  private static InvalidDataException ProtocolError() => new(Text.Get("ProtocolFailure"));
  private static string ServerMessage(ApiResponse response) => Text.Get("ServerError", response.Error ?? response.ResultCode.ToString(), response.Message ?? Text.Get("ProtocolFailure"));
  private static string Amount(long? cents) => cents is { } value ? Pricing.Dollars(value) : Text.Get("InvalidTotal");
}
