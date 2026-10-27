using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.App.ViewModels;

/// <summary>
/// 右键菜单和工具栏「+子项 / +同级」共用的类型选项表，顺序即菜单顺序。
/// 只给元素代号（L / A / U4 …）：SECS-II 工程师看代号就够，带上中文会让
/// 15 格排不进 3 行，窗口压到下限时还得滚动。
/// </summary>
public sealed record ItemTypeOption(SecsItemType Type, string Header)
{
    public static IReadOnlyList<ItemTypeOption> All { get; } =
    [
        new(SecsItemType.List, "L"),
        new(SecsItemType.A, "A"),
        new(SecsItemType.B, "B"),
        new(SecsItemType.Boolean, "Boolean"),
        new(SecsItemType.J, "J"),
        new(SecsItemType.I1, "I1"),
        new(SecsItemType.I2, "I2"),
        new(SecsItemType.I4, "I4"),
        new(SecsItemType.I8, "I8"),
        new(SecsItemType.U1, "U1"),
        new(SecsItemType.U2, "U2"),
        new(SecsItemType.U4, "U4"),
        new(SecsItemType.U8, "U8"),
        new(SecsItemType.F4, "F4"),
        new(SecsItemType.F8, "F8")
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
    private bool _isEditing;

    public StructureNodeViewModel(StructureNode node, StructureNodeViewModel? parent, MainViewModel owner)
    {
        Node = node;
        Element = node.Element;
        Parent = parent;
        Owner = owner;
        owner.RegisterNode(this);

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
    /// 就地编辑态：双击行或 F2 打开，点到这一行以外、选中换到别的节点、整棵树重建时关掉。
    /// 只管显不显示行内那两个框，不管提交时机——名称和值还是走 EditName / EditValue
    /// 那对 setter，边敲边写，和底部那两个框同一套语义（用户 2026-10-18 定）。
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>
    /// TreeViewItem.IsSelected 双向绑到这里。选中变化要汇到 Owner 一处（由它按段落进对应那棵树），
    /// 否则右键菜单、快捷键和删除各自读到的"选中节点"会对不上。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
            {
                Owner.OnNodeSelected(this);
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

    /// <summary>树行上的值摘要。大值只报大小，不把整段正文拉进界面；数组末尾点个数的标记，行内不用点开就知道填了几项。</summary>
    public string ValuePreview => Node.ItemType switch
    {
        SecsItemType.List or null => string.Empty,
        _ when Node.IsLargeValue => $"{Node.ValueSizeText} · 大值",
        _ when Node.IsArray => $"{OneLine(Node.ValueText)} · {Node.Items.Count} 个值",
        _ => OneLine(Node.SingleValue)
    };

    public bool IsLargeValue => Node.IsLargeValue;

    /// <summary>大值在底部那一栏显示的说明。正文本身不进任何 TextBox。</summary>
    public string LargeValueSummary => Node.IsLargeValue
        ? $"{Node.ElementName} 值 {Node.ValueSizeText}，不进输入框。"
        : string.Empty;

    public bool ShowsLargeValuePanel => Node.IsValueEditable && Node.IsLargeValue;

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

    /// <summary>
    /// 已经在字典里的节点不给「存为条目」：那一条本来就在字典里，再存一次是给自己复制一份，
    /// 菜单里留着只会让人以为还要按这个才算保存。要另一条同级条目用「+同级」。
    /// </summary>
    public Visibility SaveAsEntryVisibility =>
        SecsSections.Of(Element) == SecsSection.Dictionary ? Visibility.Collapsed : Visibility.Visible;

    // ---------- 属性编辑 ----------

    /// <summary>Boolean 的下拉只有两个合法值，从源头就不让填出错的。</summary>
    public IReadOnlyList<string> BooleanOptions { get; } = ["True", "False"];

    /// <summary>下拉只给两个合法值，从源头就不让填出错；数组不走下拉，用文本框按空格分隔编辑。</summary>
    /// <summary>
    /// Boolean 只有单值走下拉。判据用「值里有没有空格」而不是值的个数：属性框边敲边写，
    /// 刚打完 True 再打一个空格那一下值仍是单项，按个数判会当场把文本框换成下拉，光标和空格一起丢。
    /// </summary>
    public bool IsBoolean => Node.ItemType == SecsItemType.Boolean && !Node.ValueText.Contains(' ');

    public bool IsMultiLineValue => Node.ItemType is SecsItemType.A or SecsItemType.J;

    public bool ShowsValueEditor => Node.IsValueEditable && !IsBoolean && !Node.IsLargeValue;

    public bool ShowsBooleanEditor => Node.IsValueEditable && IsBoolean;

    public bool ShowsValueNote => !Node.IsValueEditable;

    /// <summary>没有值可编辑时，值那一栏显示的说明文字。</summary>
    public string ValueNote => Node.ItemType switch
    {
        SecsItemType.List => $"子项 {Node.Children.Count} 个",
        _ => "未知元素类型，v1 不编辑。"
    };

    /// <summary>
    /// 值输入框的悬停说明。有问题先报问题，没问题才讲怎么填——数组这一条不写出来，
    /// 用户看不出这个框能装多个值（第 5 条修改意见要的就是这个能力）。
    /// </summary>
    public string? ValueEditorTip => ValidationError ?? (Node.IsLargeValue
        ? "值太长，不在输入框展开。"
        : Node.ItemType switch
        {
            SecsItemType.A or SecsItemType.J => "文本，可换行。",
            SecsItemType.B => "十六进制字节，空格分隔；0x 前缀自动去掉，负数按二补码转换。",
            _ when Node.CanHoldArray => "多值以空格分隔，如 0 0 1。",
            _ => null
        });

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
            Owner.OnStructureEdited(this);
        }
    }

    public string EditValue
    {
        // 大值不给绑定值：就算框已经折叠，WPF 仍会把 Text 送进控件，几十兆文本照样要排版。
        get => Node.IsValueEditable && !Node.IsLargeValue ? Node.ValueText : string.Empty;
        set
        {
            if (!Node.IsValueEditable || Node.IsLargeValue || value == EditValue)
            {
                return;
            }

            StructureEditor.SetValue(Element, value);
            RefreshSnapshot();
            Owner.OnStructureEdited(this);
        }
    }

    /// <summary>
    /// 收框或失焦时把 B 值里的 0x 前缀和负数换掉。不能放在 EditValue 的 setter 里逐字转换：
    /// 刚敲下 '-' 的时候后面的数字还没输入，值就已经被改写，-45 永远打不进来；
    /// 0x 也会在刚敲完 0 的那一下被拆开。
    /// </summary>
    public void CommitValue()
    {
        if (Node.ItemType != SecsItemType.B || Node.IsLargeValue)
        {
            return;
        }

        var converted = StructureNode.NormalizeBinaryText(EditValue);
        if (converted != EditValue)
        {
            EditValue = converted;
        }
    }

    /// <summary>
    /// 整段替换值。给大值用：正文不经过输入框绑定，直接写进 XML，
    /// 免得 WPF 先把它塞进 TextBox 再排版。B 的 0x 前缀在这里就去掉，导出的 SXML 保持原格式。
    /// </summary>
    public bool ReplaceValueText(string text)
    {
        if (!Node.IsValueEditable)
        {
            return false;
        }

        var stored = Node.ItemType == SecsItemType.B
            ? StructureNode.NormalizeBinaryText(text)
            : text;
        if (!StructureEditor.SetValue(Element, stored))
        {
            return false;
        }

        RefreshSnapshot();
        Owner.OnStructureEdited(this);
        return true;
    }

    /// <summary>
    /// 名字或值改完后 XElement 变了，但 VM 包的是构造时的快照。只重建自己这一行再通知，
    /// 比整棵树重建便宜，也不会把属性输入框的光标弄丢。
    /// 值的形状变了，用哪几个编辑控件也跟着变（Boolean 单值走下拉、数组走文本框），
    /// 所以那几个可见性也要一起报，不然界面会停在错的那一个控件上。
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
        OnPropertyChanged(nameof(IsBoolean));
        OnPropertyChanged(nameof(ShowsValueEditor));
        OnPropertyChanged(nameof(ShowsBooleanEditor));
        OnPropertyChanged(nameof(ShowsValueNote));
        OnPropertyChanged(nameof(ValueNote));
        OnPropertyChanged(nameof(ValueEditorTip));
        OnPropertyChanged(nameof(IsLargeValue));
        OnPropertyChanged(nameof(LargeValueSummary));
        OnPropertyChanged(nameof(ShowsLargeValuePanel));
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
        // 只摊平前 40 个字符：大值摘要每次刷新都会读这里，不能为一行预览扫整段正文。
        if (value.Length <= 40)
        {
            return value.Replace('\r', ' ').Replace('\n', ' ');
        }

        var flat = value[..40].Replace('\r', ' ').Replace('\n', ' ');
        return $"{flat}…";
    }

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
