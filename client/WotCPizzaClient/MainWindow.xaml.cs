using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WotCPizzaClient.Services;
using WotCPizzaClient.ViewModels;

namespace WotCPizzaClient
{
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window
  {
    private readonly DispatcherTimer refreshTimer = new();
    private readonly MainViewModel viewModel;
    internal bool ExitConfirmed { get; set; }

    public MainWindow()
    {
      InitializeComponent();
      viewModel = new MainViewModel(new PizzaService(), new WpfDialogs(this));
      DataContext = viewModel;
      refreshTimer.Tick += RefreshTimer_Tick;
      viewModel.RefreshTimerReset += ResetRefreshTimer;
    }

    private async void Orders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (DataContext is MainViewModel model && e.AddedItems.Count > 0 && e.AddedItems[0] is OrderEntry entry)
      {
        await model.SelectOrderAsync(entry);
      }
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
      refreshTimer.Stop();
      await viewModel.RefreshAsync();
      if (IsLoaded)
      {
        ResetRefreshTimer(this, EventArgs.Empty);
      }
    }

    private void ResetRefreshTimer(object? sender, EventArgs e)
    {
      refreshTimer.Stop();
      if (viewModel.IsSignedIn && viewModel.NextRefreshInterval is { } interval)
      {
        refreshTimer.Interval = interval;
        refreshTimer.Start();
      }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
      if (!ExitConfirmed)
      {
        e.Cancel = !viewModel.ConfirmClose();
      }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
      refreshTimer.Stop();
      refreshTimer.Tick -= RefreshTimer_Tick;
      viewModel.RefreshTimerReset -= ResetRefreshTimer;
      viewModel.Dispose();
    }
  }
}