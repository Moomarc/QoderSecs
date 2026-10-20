using System.Windows;
using System.Windows.Input;

namespace QoderSecs.App.Commands;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onFailure;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onFailure = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onFailure = onFailure;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception exception) when (_onFailure is not null)
        {
            // async void 里逃出去的异常会结束进程，未编辑完的消息库会一起丢。
            _onFailure(exception);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null)
        {
            return;
        }

        // 按钮的可用性只能在 UI 线程改。收发回调哪天从别的线程调进来，
        // 直接触发事件会抛跨线程异常，把那条回调连同连接一起带崩。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => handler(this, EventArgs.Empty));
            return;
        }

        handler(this, EventArgs.Empty);
    }
}
