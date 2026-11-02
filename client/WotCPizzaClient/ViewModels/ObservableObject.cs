using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WotCPizzaClient.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
  public event PropertyChangedEventHandler? PropertyChanged;

  protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
  {
    if (EqualityComparer<T>.Default.Equals(field, value))
    {
      return false;
    }

    field = value;
    OnPropertyChanged(name);
    return true;
  }

  protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
  public event EventHandler? CanExecuteChanged;

  /// <summary>Reports whether the command is currently available.</summary>
  public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

  /// <summary>Executes an available command.</summary>
  public void Execute(object? parameter)
  {
    if (CanExecute(parameter))
    {
      execute(parameter);
    }
  }

  /// <summary>Refreshes bound controls after state changes.</summary>
  public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null) : ICommand
{
  private bool running;
  public event EventHandler? CanExecuteChanged;

  /// <summary>Reports whether the command can start without overlapping its previous invocation.</summary>
  public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke(parameter) ?? true);

  /// <summary>WPF command entry point; all asynchronous work is awaited.</summary>
  public async void Execute(object? parameter) => await ExecuteAsync(parameter);

  /// <summary>Runs the command with reentrancy protection.</summary>
  public async Task ExecuteAsync(object? parameter = null)
  {
    if (!CanExecute(parameter))
    {
      return;
    }

    running = true;
    Refresh();

    try
    {
      await execute(parameter);
    }
    finally
    {
      running = false;
      Refresh();
    }
  }

  /// <summary>Refreshes bound controls after state changes.</summary>
  public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
