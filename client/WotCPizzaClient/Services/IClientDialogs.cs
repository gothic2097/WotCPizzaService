using WotCPizzaClient.Models;

namespace WotCPizzaClient.Services;

public interface IClientDialogs
{
  /// <summary>Shows a blocking informational message.</summary>
  void Show(string message);
  /// <summary>Asks for an explicit yes/no decision.</summary>
  bool Confirm(string message);
  /// <summary>Offers retry of the unchanged request or exit.</summary>
  bool Retry(string detail);
  /// <summary>Edits a detached pizza and returns null when dismissed.</summary>
  PizzaOrder? EditPizza(Menu menu, PizzaOrder? pizza);
  /// <summary>Displays a read-only, copyable description.</summary>
  void ShowReceipt(string description);
  /// <summary>Closes the application after the user chooses exit.</summary>
  void Exit();
}
