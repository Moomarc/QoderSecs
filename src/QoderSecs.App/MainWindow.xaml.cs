using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using QoderSecs.App.ViewModels;
using QoderSecs.Core.Connection;

namespace QoderSecs.App;

public partial class MainWindow : Window
{
    private bool _canClose;

    /// <summary>列表停在中途时不再把视图拽回最新一条，回到底部后自动恢复跟随。</summary>
    private bool _followLatest = true;

    /// <summary>详情区同样智能滚动；追加时跟随，换一条记录就回到顶部。</summary>
    private bool _followDetail = true;

    private string _previousDetail = string.Empty;

    private ObservableCollection<CommunicationRecord>? _recordsSource;

    private MainViewModel? _detailSource;

    public MainWindow()
    {
        InitializeComponent();

        // Insert 键开菜单时临时改成贴着节点弹；关掉后恢复跟鼠标，
        // 否则右键菜单会跑到节点下面去，不像右键菜单。
        if (FindResource("NodeContextMenu") is ContextMenu nodeMenu)
        {
            nodeMenu.Closed += (_, _) =>
                nodeMenu.Placement = PlacementMode.MousePoint;
        }

        RecordList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, e) =>
                _followLatest = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1));

        RecordDetailBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, e) =>
                _followDetail = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1));

        DataContextChanged += (_, _) =>
        {
            FollowRecords();
            FollowDetail();
        };
    }

    private void FollowDetail()
    {
        if (_detailSource is { } previous)
        {
            previous.PropertyChanged -= OnDetailChanged;
        }

        _detailSource = DataContext as MainViewModel;
        if (_detailSource is { } current)
        {
            current.PropertyChanged += OnDetailChanged;
        }
    }

    private void OnDetailChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.RecordDetail) || _detailSource is null)
        {
            return;
        }

        var text = _detailSource.RecordDetail;
        var appended = text.StartsWith(_previousDetail, StringComparison.Ordinal);
        _previousDetail = text;

        if (!appended)
        {
            // 换了一条记录：从头看，而不是停在上一条的底部。
            _followDetail = true;
            RecordDetailBox.ScrollToHome();
            return;
        }

        if (_followDetail)
        {
            _ = Dispatcher.BeginInvoke(new Action(RecordDetailBox.ScrollToEnd));
        }
    }

    /// <summary>大消息正文默认折叠，双击详情区展开或收起。</summary>
    private async void ToggleDetailExpand(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.ToggleDetailExpandAsync();
        }
    }

    private void FollowRecords()
    {
        if (_recordsSource is { } previous)
        {
            previous.CollectionChanged -= OnRecordsChanged;
        }

        _recordsSource = (DataContext as MainViewModel)?.Records;
        if (_recordsSource is { } current)
        {
            current.CollectionChanged += OnRecordsChanged;
        }
    }

    private void OnRecordsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_followLatest || e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not { Count: > 0 } added)
        {
            return;
        }

        // 容器还没生成就滚动会什么都没发生，排到本轮布局之后再滚。
        _ = Dispatcher.BeginInvoke(new Action(() => RecordList.ScrollIntoView(added[added.Count - 1])));
    }

    /// <summary>WPF 的 TreeViewItem 右键不会自动选中，不补这一步右键菜单就会作用在旧选中项上。</summary>
    private void SelectNodeOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>
    /// 消息属性框是失焦才提交的，回车本身不会离开输入框。
    /// 这里把焦点移走，让「敲完按回车」和「敲完点别处」是同一条提交路径。
    /// </summary>
    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not UIElement element)
        {
            return;
        }

        element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    /// <summary>
    /// 树的快捷键：Delete 直接删选中节点；Insert 打开选中节点的右键菜单，
    /// 键盘用户从那里进「添加子项 / 添加同级」的类型二级菜单。
    /// </summary>
    private void StructureTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Delete)
        {
            viewModel.DeleteNodeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Insert || sender is not TreeView tree)
        {
            return;
        }

        var item = tree.ItemContainerGenerator.ContainerFromItem(tree.SelectedItem) as TreeViewItem;
        if (item?.ContextMenu is { } menu)
        {
            // 菜单是资源里的共享实例，不先指定 PlacementTarget 它既不知道弹在哪，
            // 也拿不到节点的数据上下文，菜单项的绑定全是空的。
            // 右键打开时 WPF 自己按鼠标定位；键盘打开默认也落在鼠标处，得改成贴着节点。
            menu.PlacementTarget = item;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_canClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        IsEnabled = false;

        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.ShutdownAsync();
            viewModel.Dispose();
        }

        _canClose = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
}
