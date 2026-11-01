using System.Windows;

namespace WotCPizzaClient.Views;

public partial class ReceiptWindow : Window
{
  /// <summary>Displays a read-only receipt whose contents can be selected and copied.</summary>
  public ReceiptWindow(string description)
  {
    InitializeComponent();
    DataContext = description;
  }
}
