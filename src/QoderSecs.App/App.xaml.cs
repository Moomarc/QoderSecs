using System.Windows;
using System.Windows.Threading;
using QoderSecs.App.ViewModels;
using QoderSecs.Core.Profiles;

namespace QoderSecs.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var paths = new AppDataPaths(AppContext.BaseDirectory);
            var startupNotices = new[]
            {
                paths.EnsureCreated(),
                SampleLibrary.EnsureExtracted(paths.SampleLibraryPath)
            }.Where(message => message is not null).ToArray();

            var viewModel = new MainViewModel(new ConnectionSettingsStore(paths.SettingsPath));
            await viewModel.InitializeAsync();
            if (startupNotices.Length > 0)
            {
                // 放在 InitializeAsync 之后，不然刚写上去就被初始化那条状态盖掉。
                viewModel.ShowStartupNotice(string.Join(" ", startupNotices));
            }

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            // 启动路上的异常没有别的出口：async void 里逃出去就是进程直接消失，
            // 用户只看到一个没有内容的崩溃框，所以这里把原因说出来再退。
            MessageBox.Show(
                $"初始化失败（{exception.GetType().Name}）：{exception.Message}",
                "QoderSecs",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>进程一崩，没保存的消息库就一起没了，所以最后兜一层。</summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"出现未处理的异常，这一步操作已中止：{e.Exception.Message}",
            "QoderSecs",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
