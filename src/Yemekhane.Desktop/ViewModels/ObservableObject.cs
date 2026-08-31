using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Yemekhane.Desktop.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool executing;
    public event EventHandler? CanExecuteChanged;

    /// <summary>Komut govdesinden kacan hata; ana uygulama bunu kullaniciya gosterir.</summary>
    public static event EventHandler<Exception>? UnhandledError;
    internal static void ReportUnhandled(object sender, Exception exception) => UnhandledError?.Invoke(sender, exception);

    public bool CanExecute(object? parameter) => !executing && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        executing = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        catch (Exception exception)
        {
            // async void icindeki yakalanmamis hata WPF'te uygulamayi dusurur. ViewModel'ler yalnizca
            // belirli tipleri yakaliyor; beklenmeyen bir hata tum uygulamayi kapatmamali.
            UnhandledError?.Invoke(this, exception);
        }
        finally { executing = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);
    public void Execute(object? parameter) { if (parameter is T value) execute(value); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand<T>(Func<T, Task> execute, Func<T, bool>? canExecute = null) : ICommand
{
    private bool executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !executing && parameter is T value && (canExecute?.Invoke(value) ?? true);
    public async void Execute(object? parameter)
    {
        if (parameter is not T value || !CanExecute(value)) return;
        executing = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(value); }
        catch (Exception exception) { AsyncCommand.ReportUnhandled(this, exception); }
        finally { executing = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
