using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

        // 行内编辑的文本框不收换行，回车冒泡到这里：收框、把 B 类负数转掉，
        // 焦点交给整棵树，接着按 F2 / Delete 都不用再点一下。
        if (e.Key == Key.Enter && viewModel.EndStructureEdit())
        {
            if (sender is TreeView treeView)
            {
                treeView.Focus();
            }

            e.Handled = true;
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
    /// 不需要在这里放过它。名称和值是边敲边写的，但 B 类的负数要等收框才换成十六进制。
    /// 消息头那几个框是失焦才写库的，点空白处不会移走焦点，所以这里顺手提交一次。
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
        CommitPendingTextInput();
    }

    /// <summary>
    /// 把当前文本框里还没失焦的值写回绑定。点在同一个框里是在挪光标，不能打断；
    /// 写回之后再交掉键盘焦点，界面上那个框就真正结束编辑了。
    /// </summary>
    private void CommitPendingTextInput()
    {
        if (Keyboard.FocusedElement is not TextBox textBox || textBox.IsMouseOver)
        {
            return;
        }

        var viewModel = DataContext as MainViewModel;
        viewModel?.DeferMessageListRefresh();

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (textBox.DataContext is StructureNodeViewModel node)
        {
            node.CommitValue();
        }

        Keyboard.ClearFocus();

        if (viewModel is not null)
        {
            // 这次点击还在 PreviewMouseDown 里：等 ListBox 把选中处理完再重建左栏，
            // 否则被点的行会在选中落地之前被换掉，点击就丢了。
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(viewModel.FlushDeferredMessageListRefresh));
        }
    }

    /// <summary>结构值框失焦时收掉 B 类负数和 0x 前缀，Tab、点工具栏、切到别的程序都走这一条。</summary>
    private void StructureValueLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StructureNodeViewModel node })
        {
            node.CommitValue();
        }
    }

    /// <summary>单行值框按回车就收框；A / J 的换行框要留给换行，不能抢。</summary>
    private void StructureValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { AcceptsReturn: false } box)
        {
            return;
        }

        if (box.DataContext is StructureNodeViewModel node)
        {
            node.CommitValue();
        }

        e.Handled = true;
    }

    // ---------- 大值：不进输入框，整段替换 ----------

    private static bool TryGetTextData(IDataObject data, out string text)
    {
        text = string.Empty;
        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            text = data.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        }
        else if (data.GetDataPresent(DataFormats.Text))
        {
            text = data.GetData(DataFormats.Text) as string ?? string.Empty;
        }

        return text.Length > 0;
    }

    /// <summary>
    /// 粘进来的正文超过大值阈值就不让它进 TextBox：WPF 会先给整段文本排版，
    /// 五兆的 PP body 就是卡五秒的来源。改成直接写 XML，界面上只留摘要。
    /// </summary>
    private void ValueBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StructureNodeViewModel node })
        {
            return;
        }

        if (!TryGetTextData(e.DataObject, out var text)
            || text.Length <= StructureNode.LargeValueCharacterThreshold)
        {
            return;
        }

        e.CancelCommand();
        node.ReplaceValueText(text);
    }

    /// <summary>大文本拖到值框或大值摘要区时显示可放；小文本交给 TextBox 自己插到光标处。</summary>
    private void ValueBoxPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetTextData(e.Data, out var text))
        {
            return;
        }

        if (sender is TextBox && text.Length <= StructureNode.LargeValueCharacterThreshold)
        {
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>大文本松手整段替换，不走 TextBox 的默认插入；大值摘要区没有输入框，任何文本都按整段替换。</summary>
    private void ValueBoxPreviewDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StructureNodeViewModel node })
        {
            return;
        }

        if (!TryGetTextData(e.Data, out var text))
        {
            return;
        }

        if (sender is TextBox && text.Length <= StructureNode.LargeValueCharacterThreshold)
        {
            return;
        }

        e.Handled = true;
        node.ReplaceValueText(text);
    }

    private void ReplaceLargeValueFromClipboard(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StructureNodeViewModel node })
        {
            return;
        }

        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"读剪贴板失败：{exception.Message}",
                "替换大值",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show(this, "剪贴板里没有文本。", "替换大值", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        node.ReplaceValueText(text);
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
        ClearDropHint();
    }

    /// <summary>
    /// 光标形状只问 Core 的落点判断，不在界面里再写一份——
    /// 写两份就会出现「光标显示能放、放下去报错」和反过来那一种。
    /// 字典页跨条目那一种是复制，光标也得跟着显示成复制。
    /// 落点在目标行的上三分之一、中段、下三分之一分别换成前插、放进、后插。
    /// </summary>
    private void NodeTreeDragOver(object sender, DragEventArgs e)
    {
        var pair = TryGetDropPair(sender, e);
        e.Effects = pair is { } found && DataContext is MainViewModel viewModel
            ? DropEffectOf(viewModel.NodeDropWay(found.dragged, found.target, found.zone))
            : DragDropEffects.None;

        if (pair is { } hint && sender is TreeView tree)
        {
            ShowDropHint(tree, hint.target, hint.zone);
        }
        else
        {
            ClearDropHint();
        }

        e.Handled = true;
    }

    private static DragDropEffects DropEffectOf(DropWay way) => way switch
    {
        DropWay.Move => DragDropEffects.Move,
        DropWay.Copy => DragDropEffects.Copy,
        _ => DragDropEffects.None
    };

    private void NodeTreeDragLeave(object sender, DragEventArgs e) => ClearDropHint();

    private void NodeTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var pair = TryGetDropPair(sender, e);
        ClearDropHint();

        if (pair is not { } found || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.DropNode(found.dragged, found.target, found.zone);
    }

    /// <summary>拖出来的行、鼠标底下的行和落在哪一段，三个都成立才成对。</summary>
    private (StructureNodeViewModel dragged, StructureNodeViewModel target, DropZone zone)? TryGetDropPair(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not TreeView tree
            || e.Data.GetData(StructureNodeDragFormat) is not StructureNodeViewModel dragged
            || TargetNodeUnderMouse(tree, e) is not { } target
            || RowHeader(tree, target) is not { } header)
        {
            return null;
        }

        var zone = ZoneUnderMouse(header, e);
        return viewModel.NodeDropWay(dragged, target, zone) == DropWay.Nowhere
            ? null
            : (dragged, target, zone);
    }

    /// <summary>
    /// 按鼠标在目标行头部的位置分三段。用的是 TreeViewItem 的 PART_Header 而不是整只容器：
    /// 展开的父项，容器高度把一整排子孙都算进去了，拿它分三段会把鼠标在子行上的位置算成父项的下段。
    /// </summary>
    private static DropZone ZoneUnderMouse(FrameworkElement header, DragEventArgs e)
    {
        if (header.ActualHeight <= 0)
        {
            return DropZone.Middle;
        }

        var y = e.GetPosition(header).Y;
        var third = header.ActualHeight / 3;
        return y < third ? DropZone.Top
            : y >= third * 2 ? DropZone.Bottom
            : DropZone.Middle;
    }

    private DropHintAdorner? _dropHint;
    private AdornerLayer? _dropHintLayer;
    private FrameworkElement? _dropHintHost;

    /// <summary>同一条落点线反复画在同一个元素上时只改区域，不重建装饰器。</summary>
    private void ShowDropHint(TreeView tree, StructureNodeViewModel target, DropZone zone)
    {
        if (RowHeader(tree, target) is not { } header)
        {
            ClearDropHint();
            return;
        }

        if (_dropHint is not null && ReferenceEquals(_dropHintHost, header))
        {
            _dropHint.Zone = zone;
            return;
        }

        ClearDropHint();
        var layer = AdornerLayer.GetAdornerLayer(header);
        if (layer is null)
        {
            return;
        }

        _dropHint = new DropHintAdorner(header) { Zone = zone };
        layer.Add(_dropHint);
        _dropHintLayer = layer;
        _dropHintHost = header;
    }

    private void ClearDropHint()
    {
        if (_dropHint is not null && _dropHintLayer is not null)
        {
            _dropHintLayer.Remove(_dropHint);
        }

        _dropHint = null;
        _dropHintLayer = null;
        _dropHintHost = null;
    }

    private static FrameworkElement? RowHeader(TreeView tree, StructureNodeViewModel target)
    {
        if (RowContainer(tree, target) is not { } container)
        {
            return null;
        }

        return container.Template?.FindName("PART_Header", container) as FrameworkElement
            ?? container;
    }

    /// <summary>ItemContainerGenerator 只管直接子项，深层节点要一层层往下找。</summary>
    private static TreeViewItem? RowContainer(ItemsControl parent, StructureNodeViewModel target)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem direct)
        {
            return direct;
        }

        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem child)
            {
                continue;
            }

            if (RowContainer(child, target) is { } found)
            {
                return found;
            }
        }

        return null;
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
