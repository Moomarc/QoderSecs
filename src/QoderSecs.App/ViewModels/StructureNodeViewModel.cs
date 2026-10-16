using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.App.ViewModels;

/// <summary>右键菜单和工具栏「＋子项 / ＋同级」共用的类型选项表，顺序即菜单顺序。</summary>
public sealed record ItemTypeOption(SecsItemType Type, string Header)
{
    public static IReadOnlyList<ItemTypeOption> All { get; } =
    [
        new(SecsItemType.List, "L 列表"),
        new(SecsItemType.A, "A 文本"),
        new(SecsItemType.B, "B 二进制"),
        new(SecsItemType.Boolean, "Boolean 布尔"),
        new(SecsItemType.J, "J JIS 文本"),
        new(SecsItemType.I1, "I1 有符号整数"),
        new(SecsItemType.I2, "I2 有符号整数"),
        new(SecsItemType.I4, "I4 有符号整数"),
        new(SecsItemType.I8, "I8 有符号整数"),
        new(SecsItemType.U1, "U1 无符号整数"),
        new(SecsItemType.U2, "U2 无符号整数"),
        new(SecsItemType.U4, "U4 无符号整数"),
        new(SecsItemType.U8, "U8 无符号整数"),
        new(SecsItemType.F4, "F4 浮点"),
        new(SecsItemType.F8, "F8 浮点")
    ];
}

/// <summary>
/// 结构树的一行。包的是 StructureNode 的快照，树每次改动后整体重建，
/// 所以这里不需要监听 XElement 的变化——重建比增量同步简单也不会漏。
/// </summary>
public sealed class StructureNodeViewModel : ObservableObject
{
    private static readonly SolidColorBrush ListBrush = FrozenBrush(0x64, 0x74, 0x8B);
    private static readonly SolidColorBrush TextBrush = FrozenBrush(0x16, 0xA3, 0x4A);
    private static readonly SolidColorBrush BinaryBrush = FrozenBrush(0xD9, 0x77, 0x06);
    private static readonly SolidColorBrush BooleanBrush = FrozenBrush(0x93, 0x33, 0xEA);
    private static readonly SolidColorBrush UnknownBrush = FrozenBrush(0x9C, 0xA3, 0xAF);
    private static readonly SolidColorBrush NumericBrush = FrozenBrush(0x25, 0x63, 0xEB);

    private bool _isExpanded = true;
    private bool _isSelected;

    public StructureNodeViewModel(StructureNode node, StructureNodeViewModel? parent, MainViewModel owner)
    {
        Node = node;
        Element = node.Element;
        Parent = parent;
        Owner = owner;
        owner.RegisterStructureNode(this);

        foreach (var child in node.Children)
        {
            Children.Add(new StructureNodeViewModel(child, this, owner));
        }
    }

    public StructureNode Node { get; private set; }

    public XElement Element { get; }

    public StructureNodeViewModel? Parent { get; }

    public MainViewModel Owner { get; }

    public ObservableCollection<StructureNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// TreeViewItem.IsSelected 双向绑到这里。选中变化要汇到 Owner.SelectedNode 一处，
    /// 否则右键菜单、快捷键和删除各自读到的"选中节点"会对不上。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
            {
                Owner.SelectedNode = this;
            }
        }
    }

    /// <summary>色块上的字。未知类型原样显示元素名，让"v1 不编辑"的东西也看得见是什么。</summary>
    public string TypeLabel => Node.ElementName;

    public SolidColorBrush BadgeBrush => Node.ItemType switch
    {
        SecsItemType.List => ListBrush,
        SecsItemType.A or SecsItemType.J => TextBrush,
        SecsItemType.B => BinaryBrush,
        SecsItemType.Boolean => BooleanBrush,
        null => UnknownBrush,
        _ => NumericBrush
    };

    public string DisplayName => Node.Name ?? string.Empty;

    public string ValuePreview => Node.ItemType switch
    {
        SecsItemType.List or null => string.Empty,
        _ when Node.IsArray => $"{Node.Values.Count} 个值 · 数组不可编辑",
        _ => OneLine(Node.SingleValue)
    };

    public bool IsInvalid => Node.ValidationError is not null;

    public string? ValidationError => Node.ValidationError;

    // ---------- 菜单可见性 ----------

    /// <summary>
    /// 这一行允不允许加子项。判断直接借 Core 的 CanAddChild，和 AddChildCommand 的
    /// CanExecute 同源——菜单显示不显示、点下去生不生效，必须是同一条规则。
    /// 空 &lt;Structure&gt; 也算可以，那是新建消息搭出第一个节点的唯一入口。
    /// </summary>
    public bool CanAddChild => StructureEditor.CanAddChild(Element);

    /// <summary>一条消息只有一个根，根节点上没有同级可加。</summary>
    public bool CanAddSibling => StructureEditor.CanAddSibling(Element);

    public Visibility AddChildVisibility => CanAddChild ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AddSiblingVisibility => CanAddSibling ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>两个添加项都收起来时，分隔线不能一个人留在菜单顶上。</summary>
    public Visibility AddSeparatorVisibility =>
        CanAddChild || CanAddSibling ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 属性编辑 ----------

    /// <summary>Boolean 的下拉只有两个合法值，从源头就不让填出错的。</summary>
    public IReadOnlyList<string> BooleanOptions { get; } = ["True", "False"];

    public bool IsBoolean => Node.ItemType == SecsItemType.Boolean;

    public bool IsMultiLineValue => Node.ItemType is SecsItemType.A or SecsItemType.J;

    public bool ShowsValueEditor => Node.IsValueEditable && !IsBoolean;

    public bool ShowsBooleanEditor => Node.IsValueEditable && IsBoolean;

    public bool ShowsValueNote => !Node.IsValueEditable;

    /// <summary>没有值可编辑时，值那一栏显示的说明文字。</summary>
    public string ValueNote => Node.ItemType switch
    {
        SecsItemType.List => $"子项 {Node.Children.Count} 个",
        _ when Node.IsArray => $"{Node.Values.Count} 个值 · 数组不可编辑",
        _ => "未知元素类型，v1 不编辑。"
    };

    public string EditName
    {
        get => DisplayName;
        set
        {
            if (value == EditName)
            {
                return;
            }

            StructureEditor.SetName(Element, value);
            RefreshSnapshot();
            Owner.OnStructureEdited();
        }
    }

    public string EditValue
    {
        get => Node.IsValueEditable && Node.Values.Count > 0 ? Node.Values[0] : string.Empty;
        set
        {
            if (!Node.IsValueEditable || value == EditValue)
            {
                return;
            }

            StructureEditor.SetValue(Element, value);
            RefreshSnapshot();
            Owner.OnStructureEdited();
        }
    }

    /// <summary>
    /// 名字或值改完后 XElement 变了，但 VM 包的是构造时的快照。只重建自己这一行再通知，
    /// 比整棵树重建便宜，也不会把属性输入框的光标弄丢。
    /// </summary>
    public void RefreshSnapshot()
    {
        Node = new StructureNode(Element);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ValuePreview));
        OnPropertyChanged(nameof(IsInvalid));
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(EditName));
        OnPropertyChanged(nameof(EditValue));
        OnPropertyChanged(nameof(ValueNote));
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>
    /// 树行的自动化名称。不给的话 UIA 拿到的名字是 VM 的 ToString，
    /// 界面走查和读屏软件都读不出这一行是什么。
    /// </summary>
    public string AutomationName =>
        string.Join(' ', new[] { TypeLabel, DisplayName, ValuePreview }.Where(part => part.Length > 0));

    private static string OneLine(string value)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 40 ? flat : $"{flat[..40]}…";
    }

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
