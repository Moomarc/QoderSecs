using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QoderSecs.App.ViewModels;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Messages;

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
        if (!_followLatest || e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove))
        {
            return;
        }

        // 追加要贴到最新一条，上限挤掉最老那条也一样：头部少一行时视口不会自己跟到底，
        // 列表就卡在离底部差一行的地方。两种情况收成同一个动作。
        // 容器还没生成就滚动会什么都没发生，排到本轮布局之后再滚。
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_recordsSource is { Count: > 0 } records)
            {
                RecordList.ScrollIntoView(records[records.Count - 1]);
            }
        }));
    }

    /// <summary>WPF 的 TreeViewItem 右键不会自动选中，不补这一步右键菜单就会作用在旧选中项上。</summary>
    private void SelectNodeOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>同上一条理由，左栏也一样：右键不先选中，删除就会落到之前选中的那条上。</summary>
    private void SelectMessageOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>
    /// 左栏的快捷键：Delete 删选中的那条消息（确认框在命令里弹）。
    /// 只绑在这个列表上，中栏树的 Delete 还是删节点，两边不串台。
    /// </summary>
    private void MessageListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.DeleteMessageCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// 拖放数据里能不能取出一个消息库文件。后缀判定走 Core 的 LibraryFile.HasLibraryExtension，
    /// 和 VM 松手之后那次判断是同一个方法——各写一份就会出现「光标显示能松手、松手了却说不是库文件」。
    /// 这里不读文件内容，悬停阶段只按文件名决定。
    /// </summary>
    private static (string path, int count)? ReadDroppedLibrary(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return null;
        }

        return LibraryFile.HasLibraryExtension(paths[0]) ? (paths[0], paths.Length) : null;
    }

    private void MessageAreaDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ReadDroppedLibrary(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>确认框和导入都在 VM 里，这里只负责从拖放数据里取路径。</summary>
    private async void MessageAreaDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var dropped = ReadDroppedLibrary(e.Data);
        if (dropped is null || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        await viewModel.ImportDroppedFileAsync(dropped.Value.path, dropped.Value.count);
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
    /// 树的快捷键：Delete 直接删选中节点；F2 就地编辑选中那一行；Insert 打开选中节点的右键菜单，
    /// 键盘用户从那里进「添加子项 / 添加同级」的类型二级菜单；Ctrl+C / Ctrl+V 复制粘贴选中子树。
    /// </summary>
    private void NodeTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // 就地编辑的输入框里 Ctrl+C / Ctrl+V 是复制粘贴文本，不能抢成节点的复制粘贴。
        if (e.OriginalSource is not TextBox && e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.C && viewModel.CopyNodeCommand.CanExecute(null))
            {
                viewModel.CopyNodeCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V && viewModel.PasteNodeCommand.CanExecute(null))
            {
                viewModel.PasteNodeCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Delete)
        {
            viewModel.DeleteNodeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2 && viewModel.SelectedNode is { } selected)
        {
            viewModel.BeginStructureEdit(selected);
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

    /// <summary>
    /// 双击行进就地编辑。从鼠标底下的元素往上找它所属的那一行，不用 SelectedNode：
    /// 双击展开箭头那一下也会冒泡到这里，按 SelectedNode 就会把编辑开在另一行上。
    /// 找到箭头就放过，那一下是给展开/折叠用的。
    /// </summary>
    private void NodeTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || e.OriginalSource is not Visual source)
        {
            return;
        }

        for (Visual? ancestor = source; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor) as Visual)
        {
            if (ancestor is ToggleButton)
            {
                return;
            }

            if (ancestor is TreeViewItem { DataContext: StructureNodeViewModel node })
            {
                viewModel.BeginStructureEdit(node);
                return;
            }
        }
    }

    /// <summary>
    /// 按下去的位置不在正在编辑那一行里面，就把行内编辑收掉。
    /// 树的空白处、工具栏、别的页签都算「行外」；右键菜单在自己的弹出窗口里，走不到这一条，
    /// 不需要在这里放过它。名称和值是边敲边写的，收框不用提交，也不回滚。
    /// 这里不 Handled，选中、展开和起拖照旧。
    /// </summary>
    private void WindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.OriginalSource is Visual source)
        {
            for (Visual? ancestor = source; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor) as Visual)
            {
                if (ancestor is TreeViewItem { DataContext: StructureNodeViewModel { IsEditing: true } })
                {
                    return;
                }
            }
        }

        viewModel.EndStructureEdit();
    }

    // ---------- 结构树拖动：搬过去，或者留一份、放下去的是副本 ----------

    /// <summary>只在同进程内用：拖动的数据里装的是行 VM，出不了这个程序。</summary>
    private const string StructureNodeDragFormat = "QoderSecs.StructureNode";

    private StructureNodeViewModel? _dragCandidate;

    private Point _dragStart;

    /// <summary>
    /// 记下起拖候选行。在输入框里按下鼠标是在选文字、在箭头上按下是展开折叠，两种都不起拖。
    /// 这里不 Handled，否则 WPF 自己的选中和展开就没了。
    /// </summary>
    private void NodeTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;

        if (e.OriginalSource is TextBox || e.OriginalSource is not Visual source)
        {
            return;
        }

        for (Visual? ancestor = source; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor) as Visual)
        {
            if (ancestor is ToggleButton)
            {
                return;
            }

            if (ancestor is TreeViewItem { DataContext: StructureNodeViewModel node })
            {
                _dragCandidate = node;
                _dragStart = e.GetPosition(null);
                return;
            }
        }
    }

    private void NodeTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } candidate || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(null) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = null;
        _ = DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(StructureNodeDragFormat, candidate), DragDropEffects.Move | DragDropEffects.Copy);
    }

    /// <summary>
    /// 光标形状只问 Core 的落点判断，不在界面里再写一份——
    /// 写两份就会出现「光标显示能放、放下去报错」和反过来那一种。
    /// 字典页跨条目那一种是复制，光标也得跟着显示成复制。
    /// </summary>
    private void NodeTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDropPair(sender, e) is (var dragged, var target) && DataContext is MainViewModel viewModel
            ? DropEffectOf(viewModel.NodeDropWay(dragged, target))
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static DragDropEffects DropEffectOf(DropWay way) => way switch
    {
        DropWay.Move => DragDropEffects.Move,
        DropWay.Copy => DragDropEffects.Copy,
        _ => DragDropEffects.None
    };

    private void NodeTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (TryGetDropPair(sender, e) is not (var dragged, var target) || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.DropNode(dragged, target);
    }

    /// <summary>拖出来的行和鼠标底下的行，两个都在同一棵树上且落点收得下才算成对。</summary>
    private (StructureNodeViewModel dragged, StructureNodeViewModel target)? TryGetDropPair(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not TreeView tree
            || e.Data.GetData(StructureNodeDragFormat) is not StructureNodeViewModel dragged
            || TargetNodeUnderMouse(tree, e) is not { } target)
        {
            return null;
        }

        return viewModel.NodeDropWay(dragged, target) == DropWay.Nowhere
            ? null
            : (dragged, target);
    }

    /// <summary>
    /// 落点按鼠标坐标在树上命中，不用 e.OriginalSource：拖放事件的 OriginalSource 给的是
    /// 开了 AllowDrop 的那一层（整棵树），从它往上找不到任何一行，光标就永远是「不能放」。
    /// 树从参数上来——结构页和字典页那一棵共用这几个处理器，写死一棵的名字就会
    /// 「在字典那棵上拖动，命中算到结构那棵去」。
    /// </summary>
    private static StructureNodeViewModel? TargetNodeUnderMouse(TreeView tree, DragEventArgs e) =>
        NodeUnderMouse(tree.InputHitTest(e.GetPosition(tree)) as DependencyObject);

    /// <summary>从鼠标底下的元素往上找到它所属的那一行。</summary>
    private static StructureNodeViewModel? NodeUnderMouse(object? source)
    {
        for (Visual? ancestor = source as Visual; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor) as Visual)
        {
            if (ancestor is TreeViewItem { DataContext: StructureNodeViewModel node })
            {
                return node;
            }
        }

        return null;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_canClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (DataContext is MainViewModel viewModel)
        {
            // 落盘加断开大约要一秒，这期间整窗是禁用的，看着就像卡死。
            // 先在状态栏写一句，再禁用；禁用只灰掉可交互控件，状态栏那行字还看得清。
            viewModel.ShowShutdownNotice();
            IsEnabled = false;
            await viewModel.ShutdownAsync();
            viewModel.Dispose();
        }
        else
        {
            IsEnabled = false;
        }

        _canClose = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
}
