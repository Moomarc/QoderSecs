using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QoderSecs.App.ViewModels;

namespace QoderSecs.App;

/// <summary>
/// 工具栏上的类型下拉：点一下展开，点外面或者按 Esc 才收。
/// 不用 MenuItem 的子菜单，是因为那种子菜单的开关归 Menu 管，鼠标划到别处就会自己收起，
/// 外面改不动；这里换成自己控制的 Popup。类型格固定 5 列排 3 行，尺寸和右键菜单那套对齐。
/// 键盘焦点一直留在那只按钮上，格子里的高亮由 IsActive 画：弹出层是独立 HWND，
/// 焦点一进去，方向键和回车就交给它自己的窗口，主窗这边什么也收不到。
/// </summary>
public partial class TypePicker : UserControl
{
    /// <summary>键盘走到哪一格。焦点不进弹出层，所以拿它画高亮。</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(TypePicker),
        new PropertyMetadata(false));

    /// <summary>按钮上的字。</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(TypePicker));

    /// <summary>按钮外观由使用处给：工具栏那套样式在 MainWindow 的资源里，这里拿不到。</summary>
    public static readonly DependencyProperty ToggleStyleProperty = DependencyProperty.Register(
        nameof(ToggleStyle),
        typeof(Style),
        typeof(TypePicker),
        new PropertyMetadata(null, (d, e) => ((TypePicker)d).Toggle.Style = (Style?)e.NewValue));

    /// <summary>要列出来的类型。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(TypePicker),
        new PropertyMetadata(null, (d, _) => ((TypePicker)d).RebuildItems()));

    /// <summary>选中一个类型后执行的命令，参数是那个类型。</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command),
        typeof(ICommand),
        typeof(TypePicker));

    /// <summary>按当前顺序排好的类型，下标和 TypePanel 的子项一一对应。</summary>
    private readonly List<ItemTypeOption> _options = [];

    /// <summary>每一行在 _options 里的下标；上下移动按固定列数换行。</summary>
    private readonly List<List<int>> _rows = [];

    private int _activeIndex = -1;

    public TypePicker()
    {
        InitializeComponent();
    }

    public static void SetIsActive(DependencyObject element, bool value) =>
        element.SetValue(IsActiveProperty, value);

    public static bool GetIsActive(DependencyObject element) =>
        (bool)element.GetValue(IsActiveProperty);

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Style? ToggleStyle
    {
        get => (Style?)GetValue(ToggleStyleProperty);
        set => SetValue(ToggleStyleProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private void RebuildItems()
    {
        TypePanel.Children.Clear();
        _options.Clear();
        _rows.Clear();
        _activeIndex = -1;
        if (ItemsSource is null)
        {
            return;
        }

        var style = (Style)FindResource("PickerItemStyle");
        var contentTemplate = TryFindResource("TypeOptionContentTemplate") as DataTemplate;
        foreach (var item in ItemsSource)
        {
            if (item is not ItemTypeOption option)
            {
                continue;
            }

            _options.Add(option);
            var button = new Button
            {
                Content = option,
                DataContext = option,
                Tag = option,
                Style = style,
                ContentTemplate = contentTemplate
            };
            // 内容是对象而不是字符串，UIA 不会自己取 Header，必须显式给一个念得出来的名字。
            AutomationProperties.SetName(button, option.Header);
            button.Click += TypeClick;
            TypePanel.Children.Add(button);
        }

        BuildRows();
    }

    /// <summary>固定每行 5 格切成行，给上下方向键用。</summary>
    private void BuildRows()
    {
        for (var start = 0; start < _options.Count; start += TypeMenuPanel.Columns)
        {
            _rows.Add([]);
            var end = Math.Min(start + TypeMenuPanel.Columns, _options.Count);
            for (var index = start; index < end; index++)
            {
                _rows[^1].Add(index);
            }
        }
    }

    /// <summary>
    /// 点按钮开合。鼠标点的那一下不能同步开：ButtonBase 处理完 Click 还会收掉自己的鼠标捕获，
    /// 而 StaysOpen=False 的弹出层正是靠捕获感知「点外面」，同步开等于刚开就被这一下带走。
    /// 推到本轮输入处理之后再开，键盘和鼠标两条路就都留得住。
    /// </summary>
    private void ToggleClick(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => TypeDrop.IsOpen = !TypeDrop.IsOpen));

    private void DropOpened(object? sender, EventArgs e)
    {
        SetActive(-1);

        // 键盘开的直接停在第一格，接着方向键就能走；鼠标开的不点不亮。
        if (TypePanel.Children.Count > 0 && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            SetActive(0);
        }
    }

    private void DropClosed(object? sender, EventArgs e) => SetActive(-1);

    /// <summary>
    /// 键盘都在这一侧收：焦点从没离开按钮，所以弹出层里收不到键。
    /// 收起时只认 Esc，展开后 Esc 收、方向键在分组网格里走、Enter / Space 落项。
    /// </summary>
    private void RootPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!TypeDrop.IsOpen)
        {
            // Button 自己只认空格，回车要这里补一脚，不然键盘用户开不下拉。
            if (e.Key == Key.Enter)
            {
                TypeDrop.IsOpen = true;
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            CloseAndReturnFocus();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            // Button 那一格只是个画高亮用的按钮，落项也在这里补，键盘和鼠标走同一个 Activate。
            if (_activeIndex >= 0 && _activeIndex < TypePanel.Children.Count
                && TypePanel.Children[_activeIndex] is Button { Tag: ItemTypeOption option })
            {
                Activate(option);
                e.Handled = true;
            }

            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            MoveActive(e.Key);
            e.Handled = true;
        }
    }

    private void MoveActive(Key key)
    {
        if (_options.Count == 0)
        {
            return;
        }

        if (_activeIndex < 0)
        {
            SetActive(key == Key.Up ? _options.Count - 1 : 0);
            return;
        }

        var (rowIndex, column) = Locate(_activeIndex);
        switch (key)
        {
            case Key.Left when column > 0:
                SetActive(_rows[rowIndex][column - 1]);
                break;
            case Key.Right when column + 1 < _rows[rowIndex].Count:
                SetActive(_rows[rowIndex][column + 1]);
                break;
            case Key.Up when rowIndex > 0:
                SetActive(NearestInRow(_rows[rowIndex - 1], column));
                break;
            case Key.Down when rowIndex + 1 < _rows.Count:
                SetActive(NearestInRow(_rows[rowIndex + 1], column));
                break;
        }
    }

    /// <summary>
    /// 换行时列数固定是 5，最后一行可能不满；落点取目标行里最接近原列的那一格，
    /// 而不是落空或跳回开头。
    /// </summary>
    private static int NearestInRow(List<int> row, int column) =>
        row[Math.Min(column, row.Count - 1)];

    private (int Row, int Column) Locate(int index)
    {
        for (var row = 0; row < _rows.Count; row++)
        {
            var column = _rows[row].IndexOf(index);
            if (column >= 0)
            {
                return (row, column);
            }
        }

        return (0, 0);
    }

    private void SetActive(int index)
    {
        if (_activeIndex >= 0 && _activeIndex < TypePanel.Children.Count)
        {
            SetIsActive((DependencyObject)TypePanel.Children[_activeIndex], false);
        }

        _activeIndex = index >= 0 && index < TypePanel.Children.Count ? index : -1;
        if (_activeIndex >= 0)
        {
            SetIsActive((DependencyObject)TypePanel.Children[_activeIndex], true);
        }
    }

    private void TypeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ItemTypeOption option })
        {
            Activate(option);
        }
    }

    /// <summary>
    /// 落一项：先收下拉再执行。命令里可能弹确认框或刷状态提示，收晚了会看见下拉还挂在后面。
    /// 鼠标点和键盘 Enter 都走这里，两条路的收尾保持一致。
    /// </summary>
    private void Activate(ItemTypeOption option)
    {
        TypeDrop.IsOpen = false;
        if (Command is { } command && command.CanExecute(option.Type))
        {
            command.Execute(option.Type);
        }

        Toggle.Focus();
    }

    private void CloseAndReturnFocus()
    {
        TypeDrop.IsOpen = false;
        Toggle.Focus();
    }
}
