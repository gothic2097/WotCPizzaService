using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WotCPizzaClient.Models;
using WotCPizzaClient.ViewModels;

namespace WotCPizzaClient.Tests;

[TestClass]
public class MainViewModelTests
{
  private readonly ScriptedService service = new();
  private readonly TestDialogs dialogs = new();
  private readonly MainViewModel model;

  /// <summary>Creates a workflow with scripted external boundaries.</summary>
  public MainViewModelTests() => model = new(service, dialogs);

  /// <summary>Loads the real fixture menu before each independent test.</summary>
  [TestInitialize]
  public async Task InitializeAsync()
  {
    service.SignInWith();
    await model.SignInCommand.ExecuteAsync();
  }

  /// <summary>Cancels owned workflow resources.</summary>
  [TestCleanup]
  public void Cleanup() => model.Dispose();

  /// <summary>New drafts must not query an order that is not stored yet.</summary>
  [TestMethod]
  public void WhenNewOrderStartsThenNoNetworkCallIsMade()
  {
    model.NewOrderCommand.Execute(null);
    Assert.AreEqual(2, service.Requests.Count);
  }

  /// <summary>Empty drafts cannot be submitted.</summary>
  [TestMethod]
  public void WhenDraftIsEmptyThenSubmissionIsDisabled()
  {
    model.NewOrderCommand.Execute(null);
    Assert.IsFalse(model.SubmitCommand.CanExecute(null));
  }

  /// <summary>Dismissal of an editor cannot mutate the order.</summary>
  [TestMethod]
  public void WhenPizzaEditorIsDismissedThenNoLineIsAdded()
  {
    model.NewOrderCommand.Execute(null);
    dialogs.Pizza = null;
    model.AddPizzaCommand.Execute(null);
    Assert.AreEqual(0, model.Lines.Count);
  }

  /// <summary>Changing the tip immediately changes the displayed total.</summary>
  [TestMethod]
  public void WhenPercentageTipChangesThenRunningTotalUpdates()
  {
    StartDraft();
    model.Tip15 = true;
    Assert.AreEqual("$6.11", model.Total);
  }

  /// <summary>Fractional cents are blocked rather than rounded without consent.</summary>
  [TestMethod]
  public void WhenDollarTipHasFractionalCentThenSubmissionIsDisabled()
  {
    StartDraft();
    model.CustomDollars = "1.001";
    Assert.IsFalse(model.SubmitCommand.CanExecute(null));
  }

  /// <summary>Retains the exact GUID and payload across a lost response.</summary>
  [TestMethod]
  public async Task WhenPlacementTimesOutThenRetrySendsIdenticalJsonAsync()
  {
    StartDraft();
    service.Responses.Enqueue(() => throw new TimeoutException("Lost response"));
    service.Responses.Enqueue(() => Accepted(ResultCode.OrderAlreadyCreated));
    await model.SubmitCommand.ExecuteAsync();
    Assert.AreEqual(service.Requests[^2], service.Requests[^1]);
  }

  /// <summary>Uncertain requests disable both editing and replacement drafts.</summary>
  [TestMethod]
  public async Task WhenMutationIsUncertainThenDraftActionsAreDisabledAsync()
  {
    StartDraft();
    bool disabled = false;
    dialogs.OnRetry = () => disabled = model.HasUnresolvedMutation && !model.CanEdit && !model.NewOrderCommand.CanExecute(null);
    service.Responses.Enqueue(() => throw new IOException("Lost response"));
    service.Responses.Enqueue(() => Accepted());
    await model.SubmitCommand.ExecuteAsync();
    Assert.IsTrue(disabled);
  }

  /// <summary>Missing snapshots are not mistaken for completed mutations.</summary>
  [TestMethod]
  public async Task WhenSuccessResponseLacksOrderThenOriginalRequestIsRetriedAsync()
  {
    StartDraft();
    service.Enqueue(new() { ResultCode = ResultCode.Success });
    service.Responses.Enqueue(() => Accepted());
    await model.SubmitCommand.ExecuteAsync();
    Assert.AreEqual(1, dialogs.RetryCount);
  }

  /// <summary>An idempotent retry is applied as an accepted order.</summary>
  [TestMethod]
  public async Task WhenPlacementWasAlreadyCreatedThenCurrentSnapshotIsAppliedAsync()
  {
    StartDraft();
    service.Responses.Enqueue(() => Accepted(ResultCode.OrderAlreadyCreated));
    await model.SubmitCommand.ExecuteAsync();
    Assert.AreEqual(OrderStatus.NotStarted, model.SelectedOrder?.Snapshot?.Status);
  }

  /// <summary>Updates retain their original expected revision on retry.</summary>
  [TestMethod]
  public async Task WhenUpdateTimesOutThenExpectedRevisionIsUnchangedAsync()
  {
    await PlaceAsync();
    model.CustomDollars = "2";
    service.Responses.Enqueue(() => throw new TimeoutException("Lost update"));
    service.Responses.Enqueue(() => Accepted(revision: 2));
    await model.SubmitCommand.ExecuteAsync();
    Assert.AreEqual(service.Requests[^2], service.Requests[^1]);
  }

  /// <summary>Status refresh does not silently replace an edited draft.</summary>
  [TestMethod]
  public async Task WhenRevisionChangesThenUnsavedEditsAndOriginalRevisionAreRetainedAsync()
  {
    await PlaceAsync();
    model.CustomDollars = "2";
    var snapshot = model.SelectedOrder?.Snapshot ?? throw new InvalidOperationException();
    service.Enqueue(new() { ResultCode = ResultCode.Success, Status = OrderStatus.NotStarted, Revision = 2, TimeTillReady = 60 });
    service.Enqueue(new() { ResultCode = ResultCode.Success, CustomerOrder = snapshot with { Revision = 2 } });
    await model.RefreshAsync();
    Assert.AreEqual((200L, 1UL, true), (model.SelectedOrder?.Draft.TipCents, model.SelectedOrder?.ExpectedRevision, model.HasConflict));
  }

  /// <summary>Reconciliation requires a new user submission rather than automatic retry.</summary>
  [TestMethod]
  public async Task WhenRevisionConflictsThenSubmissionIsBlockedAsync()
  {
    await PlaceAsync();
    model.CustomDollars = "2";
    var snapshot = model.SelectedOrder?.Snapshot ?? throw new InvalidOperationException();
    service.Enqueue(new() { ResultCode = ResultCode.Fail, Error = "revision_conflict" });
    service.Enqueue(new() { ResultCode = ResultCode.Success, CustomerOrder = snapshot with { Revision = 2 } });
    await model.SubmitCommand.ExecuteAsync();
    Assert.IsFalse(model.SubmitCommand.CanExecute(null));
  }

  /// <summary>The saved snapshot wins a cancel/start race.</summary>
  [TestMethod]
  public async Task WhenKitchenStartsBeforeCancellationThenOrderRemainsStartedAsync()
  {
    await PlaceAsync();
    var snapshot = model.SelectedOrder?.Snapshot ?? throw new InvalidOperationException();
    service.Enqueue(new() { ResultCode = ResultCode.OrderAlreadyStarted, CustomerOrder = snapshot with { Revision = 2, Status = OrderStatus.Started } });
    await model.CancelCommand.ExecuteAsync();
    Assert.AreEqual(OrderStatus.Started, model.SelectedOrder?.Snapshot?.Status);
  }

  /// <summary>Historical orders use their accepted totals instead of today's prices.</summary>
  [TestMethod]
  public async Task WhenMenuVersionDiffersThenSavedTotalsAreShownAsync()
  {
    var snapshot = TestData.Snapshot() with { MenuVersion = "old" };
    var entry = new OrderEntry(snapshot.ToRequest(), snapshot);
    model.Orders.Add(entry);
    service.Enqueue(new() { ResultCode = ResultCode.Success, CustomerOrder = snapshot });
    await model.SelectOrderAsync(entry);
    Assert.AreEqual((true, "$5.32", false), (model.IsHistorical, model.Total, model.CanEdit));
  }

  /// <summary>Menu refresh changes a quote but never submits it automatically.</summary>
  [TestMethod]
  public async Task WhenMenuIsStaleThenNewPriceRequiresConfirmationAsync()
  {
    StartDraft();
    service.Enqueue(new() { ResultCode = ResultCode.Fail, Error = "stale_menu" });
    service.Enqueue(new() { ResultCode = ResultCode.Success, Menu = TestData.Menu() with { MenuVersion = "2", Pizzas = [new(0, "Small pizza", 600)] } });
    await model.SubmitCommand.ExecuteAsync();
    Assert.AreEqual(("$6.39", FunctionId.GetMenu), (model.Total, service.LastRequest.FunctionId));
  }

  /// <summary>Selected promos are revalidated when a pizza edit crosses the threshold.</summary>
  [TestMethod]
  public async Task WhenSubtotalDropsToTwentyDollarsThenSave5BlocksSubmissionAsync()
  {
    dialogs.Pizza = new(0, 5, []);
    StartDraft();
    model.PromoText = "SAVE5";
    var draft = model.SelectedOrder?.Draft ?? throw new InvalidOperationException();
    service.Enqueue(new() { ResultCode = ResultCode.PromoValid, Totals = Services.Pricing.Calculate(TestData.Menu(), draft with { PromoCodes = ["SAVE5"] }) });
    await model.ApplyPromoCommand.ExecuteAsync();
    dialogs.Pizza = new(0, 4, []);
    model.EditPizzaCommand.Execute(model.Lines[0]);
    Assert.IsFalse(model.SubmitCommand.CanExecute(null));
  }

  /// <summary>Invalid input remains visible after navigating away and back.</summary>
  [TestMethod]
  public async Task WhenSwitchingOrdersThenInvalidTipTextIsPreservedAsync()
  {
    await PlaceAsync();
    var placed = model.SelectedOrder ?? throw new InvalidOperationException();
    var snapshot = placed.Snapshot ?? throw new InvalidOperationException();
    model.CustomDollars = "1.001";
    model.NewOrderCommand.Execute(null);
    service.Enqueue(new() { ResultCode = ResultCode.Success, CustomerOrder = snapshot });
    await model.SelectOrderAsync(placed);
    Assert.AreEqual("1.001", model.CustomDollars);
  }

  /// <summary>Exit after a transport failure does not resume submission.</summary>
  [TestMethod]
  public async Task WhenUserExitsAfterTimeoutThenNoRetryIsSentAsync()
  {
    StartDraft();
    dialogs.RetryResult = false;
    service.Responses.Enqueue(() => throw new TimeoutException("Server unavailable"));
    await model.SubmitCommand.ExecuteAsync();
    Assert.IsTrue(dialogs.Exited);
  }

  /// <summary>Both new placements and accepted retries schedule an early status check.</summary>
  [DataTestMethod]
  [DataRow(ResultCode.Success)]
  [DataRow(ResultCode.OrderAlreadyCreated)]
  public async Task WhenPlacementSucceedsThenTimerResetsToFifteenSecondsAsync(ResultCode resultCode)
  {
    StartDraft();
    TimeSpan? interval = null;
    model.RefreshTimerReset += (_, _) => interval = model.NextRefreshInterval;
    service.Responses.Enqueue(() => Accepted(resultCode));

    await model.SubmitCommand.ExecuteAsync();

    Assert.AreEqual(TimeSpan.FromSeconds(15), interval);
  }

  /// <summary>Each accepted update schedules a new early refresh.</summary>
  [TestMethod]
  public async Task WhenUpdateSucceedsThenTimerResetsToFifteenSecondsAsync()
  {
    await PlaceAsync();
    await RefreshPendingOrderAsync();
    model.CustomDollars = "2";
    TimeSpan? interval = null;
    model.RefreshTimerReset += (_, _) => interval = model.NextRefreshInterval;
    service.Responses.Enqueue(() => Accepted(revision: 2));

    await model.SubmitCommand.ExecuteAsync();

    Assert.AreEqual(TimeSpan.FromSeconds(15), interval);
  }

  /// <summary>The early refresh does not change the user's configured minutes.</summary>
  [TestMethod]
  public async Task WhenSubmissionSucceedsThenConfiguredIntervalIsUnchangedAsync()
  {
    model.RefreshMinutes = "7";

    await PlaceAsync();

    Assert.AreEqual(TimeSpan.FromMinutes(7), model.RefreshInterval);
  }

  /// <summary>Completing the early refresh restores the normal timer cadence.</summary>
  [TestMethod]
  public async Task WhenPostSubmissionRefreshCompletesThenConfiguredIntervalResumesAsync()
  {
    model.RefreshMinutes = "7";
    await PlaceAsync();
    TimeSpan? interval = null;
    model.RefreshTimerReset += (_, _) => interval = model.NextRefreshInterval;

    await RefreshPendingOrderAsync();

    Assert.AreEqual(TimeSpan.FromMinutes(7), interval);
  }

  /// <summary>A rejected placement does not schedule an early status check.</summary>
  [TestMethod]
  public async Task WhenPlacementFailsThenConfiguredIntervalRemainsActiveAsync()
  {
    StartDraft();
    service.Enqueue(new() { ResultCode = ResultCode.Fail, Error = "invalid_order" });

    await model.SubmitCommand.ExecuteAsync();

    Assert.AreEqual(TimeSpan.FromMinutes(5), model.NextRefreshInterval);
  }

  private async Task RefreshPendingOrderAsync()
  {
    service.Enqueue(new() { ResultCode = ResultCode.Success, Status = OrderStatus.NotStarted, Revision = 1, TimeTillReady = 60 });
    await model.RefreshAsync();
  }

  private void StartDraft()
  {
    model.NewOrderCommand.Execute(null);
    model.AddPizzaCommand.Execute(null);
  }

  private async Task PlaceAsync()
  {
    StartDraft();
    service.Responses.Enqueue(() => Accepted());
    await model.SubmitCommand.ExecuteAsync();
  }

  private ApiResponse Accepted(ResultCode code = ResultCode.Success, ulong revision = 1) => new()
  {
    ResultCode = code,
    CustomerOrder = TestData.Snapshot(service.LastRequest.Input?.CustomerOrder ?? throw new InvalidOperationException(), revision)
  };
}
