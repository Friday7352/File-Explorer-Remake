using System.ComponentModel;
using System.Windows.Input;
using Clearspace.Models;

namespace Clearspace.Commands;

/// <summary>
/// Wraps an <see cref="IAction"/> so WPF can bind to it directly. Buttons, menu items,
/// and key bindings all consume this, which means an action only ever gets described once.
/// </summary>
public sealed class RichCommand : ObservableObject, ICommand
{
    private readonly IAction _action;

    public RichCommand(IAction action, ExplorerContext context)
    {
        _action = action;

        // Enabled state usually depends on selection or current folder, so re-evaluate
        // whenever the context moves rather than making every action subscribe itself.
        context.PropertyChanged += OnContextChanged;
    }

    public CommandCode Code => _action.Code;

    public string Label => _action.Label;

    public string Description => _action.Description;

    public string Glyph => _action.Glyph;

    public HotKey HotKey => _action.HotKey;

    public string HotKeyLabel => _action.HotKey.Label;

    public bool IsExecutable => _action.IsExecutable;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _action.IsExecutable;

    public async void Execute(object? parameter)
    {
        try
        {
            await _action.ExecuteAsync(parameter);
        }
        catch (OperationCanceledException)
        {
            // Navigation superseded by a newer one.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[{Code}] {exception}");
        }
    }

    public Task ExecuteAsync(object? parameter = null) => _action.ExecuteAsync(parameter);

    private void OnContextChanged(object? sender, PropertyChangedEventArgs e) => NotifyStateChanged();

    public void NotifyStateChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsExecutable));
    }
}
