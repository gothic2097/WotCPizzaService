using System.Windows;
using WotCPizzaClient.Models;
using WotCPizzaClient.Resources;
using WotCPizzaClient.Views;

namespace WotCPizzaClient.Services;

internal sealed class WpfDialogs(MainWindow owner) : IClientDialogs
{
  public void Show(string message) => MessageBox.Show(owner, message, Text.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);

  public bool Confirm(string message) => MessageBox.Show(owner, message, Text.Get("AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

  public bool Retry(string detail) => MessageBox.Show(owner, Text.Get("TransportFailure", detail), Text.Get("AppTitle"),
    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes;

  public PizzaOrder? EditPizza(Menu menu, PizzaOrder? pizza)
  {
    var window = new PizzaEditorWindow(menu, pizza) { Owner = owner };
    return window.ShowDialog() == true ? window.Result : null;
  }

  public void ShowReceipt(string description) => new ReceiptWindow(description) { Owner = owner }.ShowDialog();

  public void Exit()
  {
    owner.ExitConfirmed = true;
    owner.Close();
  }
}
