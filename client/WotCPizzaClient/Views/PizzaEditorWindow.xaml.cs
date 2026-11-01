using System.Windows;
using WotCPizzaClient.Models;
using WotCPizzaClient.ViewModels;

namespace WotCPizzaClient.Views;

public partial class PizzaEditorWindow : Window
{
  private readonly PizzaEditorViewModel viewModel;
  public PizzaOrder? Result { get; private set; }

  /// <summary>Creates a modal view over a detached pizza draft.</summary>
  public PizzaEditorWindow(Menu menu, PizzaOrder? pizza)
  {
    InitializeComponent();
    viewModel = new(menu, pizza);
    DataContext = viewModel;
  }

  private void Save_Click(object sender, RoutedEventArgs e)
  {
    if (!viewModel.CanSave) return;
    Result = viewModel.Build();
    DialogResult = true;
  }
}
