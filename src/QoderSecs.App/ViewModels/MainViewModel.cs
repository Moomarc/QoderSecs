using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using QoderSecs.App.Commands;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;

namespace QoderSecs.App.ViewModels;

/// <summary>传输方式下拉框的一行。显示文字和枚举分开，界面上写「SECS I」，代码里仍是 TransportKind.Secs1。</summary>
public sealed record TransportOption(TransportKind Kind, string Display);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>详情区一次最多灌这么多字符，再多 TextBox 就画不动了；剩下的指路到 sml.log。</summary>
    private const int DetailCharacterCap = 512 * 1024;

    /// <summary>SML 预览的上限，同样是 TextBox 画不动的问题；超过就只显示一句说明。</summary>
    private const int SmlPreviewCharacterCap = 256 * 1024;

    /// <summary>结构改动到刷新 SML 预览之间的等待。打字期间不断重建大消息，界面会跟不上手。</summary>
    private const int StructureRefreshDelayMilliseconds = 250;

    /// <summary>状态点的颜色。冻结过的画刷可以跨线程给绑定用，不用每次新建。</summary>
    private static readonly Brush OnlineBrush = Frozen("#16A34A");
    private static readonly Brush PeerLostBrush = Frozen("#DC2626");
    private static readonly Brush BusyBrush = Frozen("#D97706");
    private static readonly Brush OfflineBrush = Frozen("#9CA3AF");

    /// <summary>状态栏两种颜色：平时和 XAML 里的 MutedBrush 一致，报错用红。</summary>
    private static readonly Brush StatusMutedBrush = Frozen("#667085");
    private static readonly Brush StatusErrorBrush = Frozen("#DC2626");

    private readonly ConnectionSettingsStore _settingsStore;
    private readonly ConnectionLink _link = new();
    private bool _isLinkBusy;
    private bool _isSendWaiting;
    private Task? _connectInFlight;
    private LinkPhase _phase = LinkPhase.Offline;
    private CommunicationRecord? _selectedRecord;
    private readonly ListCollectionView _recordsView;
    private string _recordFilterText = string.Empty;
    private bool _recordFilterHasError;
    private int? _filterStream;
    private int? _filterFunction;
    private int _visibleRecordCount;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _saveDebounce;
    private ConnectionProfile _profile = ConnectionProfile.CreateDefault();
    private bool _loadingEditor;
    private MessageLibrary _library = MessageLibrary.Empty;
    private string _libraryPath = string.Empty;
    private string _searchText = string.Empty;
    private MessageEntry? _selectedMessage;
    /// <summary>
    /// 两棵树各自的节点登记。条目那一档和消息体那一档的元素永远是两批不同的 XElement，
    /// 分段靠 Core 的 SecsSections，界面上不写第二份判断。
    /// </summary>
    private readonly List<StructureNodeViewModel> _structureNodes = [];
    private readonly List<StructureNodeViewModel> _entryNodes = [];

    /// <summary>每棵树自己的选中项。字典页和结构页各自记一份，切页签不会把选中串到另一棵树上去。</summary>
    private StructureNodeViewModel? _selectedStructureNode;
    private StructureNodeViewModel? _selectedEntryNode;
    private StructureNodeViewModel? _editingNode;
    private int _editorTab;

    /// <summary>
    /// 结构树的剪贴板，进程内一个槽。存的是克隆体——它不挂在任何树上，
    /// 所以换消息、重新导入、删掉原节点都不会让这一份跟着变形。
    /// </summary>
    private XElement? _structureClipboard;
    private CancellationTokenSource? _structureRefresh;
    private string _pasteSmlText = string.Empty;
    private bool _loadingMessageEditor;
    private bool _refreshingMessages;
    private bool _deferMessageListRefresh;
    private int? _deferredMessageIndex;
    private string _messageName = string.Empty;
    private string _messageDescription = string.Empty;
    private string _streamText = string.Empty;
    private string _functionText = string.Empty;
    private bool _messageWBit;
    private AutoReplyOption _messageAutoReply = AutoReplyOptions.None;
    private IReadOnlyList<AutoReplyOption> _autoReplyCandidates = [AutoReplyOptions.None];
    private EndpointRole _role;
    private ConnectionMode _mode;
    private TransportKind _transport;
    private string _ipAddress = string.Empty;
    private int _port;
    private string _serialPortName = string.Empty;
    private int _baudRate;
    private int _dataBits;
    private SerialParity _parity;
    private SerialStopBits _stopBits;
    private SerialFlowControl _flowControl;
    private int _deviceId;
    private int _t1Seconds;
    private int _t2Seconds;
    private int _t3Seconds;
    private int _t4Seconds;
    private int _t5Seconds;
    private int _t6Seconds;
    private int _t7Seconds;
    private int _t8Seconds;
    private bool _linkTestEnabled;
    private int _linkTestIntervalSeconds;
    private bool _isConfigPanelOpen;
    private string _statusMessage = "正在读取连接参数…";
    private bool _statusIsError;

    public MainViewModel(ConnectionSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        CloseConfigPanelCommand = AsyncCommand(() =>
        {
            IsConfigPanelOpen = false;
            return Task.CompletedTask;
        });
        ImportLibraryCommand = AsyncCommand(ImportLibraryAsync);
        ExportLibraryCommand = AsyncCommand(ExportLibraryAsync);
        ClearLibraryCommand = AsyncCommand(() =>
        {
            ClearLibrary();
            return Task.CompletedTask;
        });
        CheckAutoReplyCommand = new RelayCommand(CheckAutoReply);
        NewMessageCommand = AsyncCommand(() =>
        {
            NewMessage();
            return Task.CompletedTask;
        });
        DeleteMessageCommand = new RelayCommand(
            DeleteMessage,
            () => SelectedMessage is not null);
        DuplicateMessageCommand = new RelayCommand(
            DuplicateMessage,
            () => SelectedMessage is not null);
        AddChildCommand = new RelayCommand<SecsItemType>(AddChild, CanAddChildToSelection);
        AddSiblingCommand = new RelayCommand<SecsItemType>(
            AddSibling,
            () => SelectedNode is not null && StructureEditor.CanAddSibling(SelectedNode.Element));
        DeleteNodeCommand = new RelayCommand(DeleteNode, () => SelectedNode is not null);
        CopyNodeCommand = new RelayCommand(CopyNode, () => SelectedNode is not null);
        PasteNodeCommand = new RelayCommand(PasteNode, CanPasteNode);
        SaveNodeAsEntryCommand = new RelayCommand(SaveNodeAsEntry, () => SelectedNode is not null);
        // 「新增数据」永远能点：字典是全局的一堆条目，不归某一条消息，
        // 库里还没有条目时也正好要靠它建第一条。
        NewEntryCommand = new RelayCommand<SecsItemType>(NewEntry);
        DeleteEntryCommand = new RelayCommand(DeleteEntry, () => SelectedDataEntry is not null);
        ApplySmlCommand = new RelayCommand(
            ApplyPastedSml,
            () => SelectedMessage is not null && PasteSmlText.Trim().Length > 0);
        ConnectCommand = AsyncCommand(ToggleConnectionAsync, () => CanToggleConnection);
        SendCommand = AsyncCommand(SendMessageAsync, () => CanSend);
        CancelSendCommand = new RelayCommand(CancelPendingWait, () => _isSendWaiting);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder, () => LogFolderPath is not null);
        ApplyRecordFilterCommand = new RelayCommand(ApplyRecordFilter, () => RecordFilterText.Trim().Length > 0);
        ClearRecordFilterCommand = new RelayCommand(
            () => ClearRecordFilter(announce: true),
            () => IsRecordFilterActive);
        _recordsView = new ListCollectionView(Records);
        RecordsView = _recordsView;
        _link.PhaseChanged += OnLinkPhaseChanged;
        _link.Transcript.RecordAdded += OnRecordAdded;
        _link.Transcript.RecordRemoved += OnRecordRemoved;

        // 载入文件时整份换掉库实例，所以给取值委托而不是存引用。
        _link.LibraryProvider = () => _library;
    }

    public ObservableCollection<MessageEntry> Messages { get; } = [];

    public IReadOnlyList<EndpointRole> EndpointRoles { get; } = Enum.GetValues<EndpointRole>();

    public IReadOnlyList<ConnectionMode> ConnectionModes { get; } = Enum.GetValues<ConnectionMode>();

    public IReadOnlyList<TransportOption> TransportOptions { get; } =
    [
        new(TransportKind.Hsms, "HSMS"),
        new(TransportKind.Secs1, "SECS I")
    ];

    public IReadOnlyList<SerialParity> SerialParities { get; } = Enum.GetValues<SerialParity>();

    public IReadOnlyList<SerialStopBits> SerialStopBitOptions { get; } = Enum.GetValues<SerialStopBits>();

    public IReadOnlyList<SerialFlowControl> SerialFlowControls { get; } = Enum.GetValues<SerialFlowControl>();

    public AsyncRelayCommand CloseConfigPanelCommand { get; }

    public AsyncRelayCommand ImportLibraryCommand { get; }

    public AsyncRelayCommand ExportLibraryCommand { get; }

    public AsyncRelayCommand ClearLibraryCommand { get; }

    public RelayCommand CheckAutoReplyCommand { get; }

    public AsyncRelayCommand NewMessageCommand { get; }

    public RelayCommand DeleteMessageCommand { get; }

    public RelayCommand DuplicateMessageCommand { get; }

    public RelayCommand<SecsItemType> AddChildCommand { get; }

    public RelayCommand<SecsItemType> AddSiblingCommand { get; }

    public RelayCommand DeleteNodeCommand { get; }

    public RelayCommand CopyNodeCommand { get; }

    public RelayCommand PasteNodeCommand { get; }

    public RelayCommand ApplySmlCommand { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand CancelSendCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand ApplyRecordFilterCommand { get; }

    public RelayCommand ClearRecordFilterCommand { get; }

    public IReadOnlyList<ItemTypeOption> ItemTypeOptions { get; } = ItemTypeOption.All;

    public ObservableCollection<StructureNodeViewModel> StructureRootItems { get; } = [];

    /// <summary>字典页那棵树的顶层：一个条目就是一个顶层节点，不垫假根行（条目之间互不隶属）。</summary>
    public ObservableCollection<StructureNodeViewModel> EntryRootItems { get; } = [];

    /// <summary>数据字典那个页签的序号。页签顺序变了改这里，界面上不另找一遍。</summary>
    public const int DictionaryTabIndex = 2;

    /// <summary>
    /// 消息编辑区当前停在哪个页签（0 结构、1 SML、2 数据字典）。
    /// 命令落在哪棵树上问它，不问「上一次点的是哪一行」——
    /// 人站在字典页却把删除加到消息结构上，是两棵树最容易出的错。
    /// </summary>
    public int EditorTab
    {
        get => _editorTab;
        set
        {
            if (!SetProperty(ref _editorTab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedNode));
            OnPropertyChanged(nameof(HasSelectedNode));
            RaiseTreeCommandCanExecute();
        }
    }

    /// <summary>
    /// 当前这棵树的选中节点。两棵树各自记一份，读的时候按页签取，
    /// 所以人站在哪一页，命令就作用在哪一页的选中项上。
    /// </summary>
    public StructureNodeViewModel? SelectedNode =>
        EditorTab == DictionaryTabIndex ? _selectedEntryNode : _selectedStructureNode;

    /// <summary>行 VM 的 IsSelected 变成 true 时走这里。落在哪一份看节点自己在哪一段，不问页签。</summary>
    internal void OnNodeSelected(StructureNodeViewModel? node)
    {
        if (node is null)
        {
            // 命令做完时那个节点可能没建成（Core 拒了），这时保持原有选中，不清另一棵的。
            return;
        }

        if (IsEntryNode(node))
        {
            SetSelectedNode(ref _selectedEntryNode, node);
            SyncSelectedEntryFromTree();
            return;
        }

        SetSelectedNode(ref _selectedStructureNode, node);
    }

    /// <summary>树里没有可选中的节点了（删到空、换消息、整棵树重建）。只清自己那一棵的选中。</summary>
    internal void ClearStructureSelection() => SetSelectedNode(ref _selectedStructureNode, null);

    internal void ClearEntrySelection() => SetSelectedNode(ref _selectedEntryNode, null);

    private void SetSelectedNode(ref StructureNodeViewModel? lane, StructureNodeViewModel? value)
    {
        if (ReferenceEquals(lane, value))
        {
            return;
        }

        var previous = lane;
        lane = value;
        if (previous is not null)
        {
            previous.IsSelected = false;
            // 选中挪走了，上一行的行内编辑就结束。改出去的名称和值留住，不弹确认也不回滚
            // （用户 2026-10-18 定：不做 Esc 取消，提交是即时的）。
            previous.IsEditing = false;
        }

        if (ReferenceEquals(_editingNode, previous))
        {
            _editingNode = null;
        }

        if (value is not null)
        {
            value.IsSelected = true;
        }

        OnPropertyChanged(nameof(SelectedNode));
        OnPropertyChanged(nameof(HasSelectedNode));
        RaiseTreeCommandCanExecute();
    }

    /// <summary>节点属于字典段还是消息段，判断只有一条：Core 的 SecsSections。</summary>
    private static bool IsEntryNode(StructureNodeViewModel node) =>
        SecsSections.Of(node.Element) == SecsSection.Dictionary;

    public bool HasSelectedNode => SelectedNode is not null;

    /// <summary>结构页签没树可显示时的提示语；有树时是空串，XAML 用它把提示隐藏掉。</summary>
    public string StructureHint => SelectedMessage is null
        ? "选择或新建消息后编辑结构"
        : StructureRootItems.Count == 0
            ? "无消息体；用「+子项」添加根节点"
            : string.Empty;

    /// <summary>
    /// SML 页签的预览。转不出来时把原因直接写进预览框——
    /// 值不合法禁止发送这条规则，在发送做出来之前先在这里看得见。
    /// 大 PP body 不生成正文，预览框装不下几十兆文本。
    /// </summary>
    public string SmlPreviewText
    {
        get
        {
            if (SelectedMessage is null)
            {
                return string.Empty;
            }

            return SmlConverter.TryToPreviewSml(SelectedMessage, SmlPreviewCharacterCap, out var sml, out var error)
                ? sml
                : $"无法生成 SML，发送已拦截：{error}";
        }
    }

    public bool SmlPreviewHasError =>
        SelectedMessage is not null && !SmlConverter.TryValidate(SelectedMessage, out _);

    public string PasteSmlText
    {
        get => _pasteSmlText;
        set
        {
            if (SetProperty(ref _pasteSmlText, value))
            {
                ApplySmlCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConfigPanelOpen
    {
        get => _isConfigPanelOpen;
        set => SetProperty(ref _isConfigPanelOpen, value);
    }

    public ObservableCollection<CommunicationRecord> Records { get; } = [];

    /// <summary>
    /// 列表实际绑的是这一层视图，不是 <see cref="Records"/> 本身：筛选只换视图，
    /// 记录照常进集合，取消筛选时被藏起来的那些原样回来。
    /// </summary>
    public ICollectionView RecordsView { get; }

    /// <summary>筛选框里的字。回车才应用，光改字不动当前筛选。</summary>
    public string RecordFilterText
    {
        get => _recordFilterText;
        set
        {
            if (SetProperty(ref _recordFilterText, value))
            {
                // 一改字就把红框撤掉：红框只标记「刚才回车那一版写法不对」，
                // 留着不放会让人以为新输入也还没生效。
                RecordFilterHasError = false;
                ApplyRecordFilterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>筛选框刚被回车校验拒绝。视图据此描红并改提示条文案。</summary>
    public bool RecordFilterHasError
    {
        get => _recordFilterHasError;
        private set => SetProperty(ref _recordFilterHasError, value);
    }

    /// <summary>只给 Stream 的筛选（S6 / 6）时 Function 为 null，视图按「整个 Stream」过滤。</summary>
    public bool IsRecordFilterActive => _filterStream is not null;

    /// <summary>应用到列表上的那一条，按界面写法补回 S 前缀，例如 S6 或 S6F11。</summary>
    public string ActiveRecordFilterText => _filterStream is { } stream
        ? _filterFunction is { } function ? $"S{stream}F{function}" : $"S{stream}"
        : string.Empty;

    /// <summary>通信记录面板空着时的提示语；有记录时是空串，XAML 靠它把提示隐藏掉。</summary>
    public string RecordsHint => Records.Count == 0
        ? "尚无通信记录"
        : IsRecordFilterActive && _visibleRecordCount == 0
            ? $"没有 {ActiveRecordFilterText} 的记录"
            : string.Empty;

    /// <summary>
    /// 本次连接的日志目录；断开后回落到本进程最后一次会话的目录。
    /// 从没连过时为 null，「查看日志」据此变灰。
    /// </summary>
    public string? LogFolderPath => _link.SessionDirectory ?? _link.LastSessionDirectory;

    /// <summary>按钮悬浮提示：说清楚打开的是当前会话还是上一次的目录。</summary>
    public string LogFolderHint => LogFolderPath is not { } path
        ? "还没有连接过，暂无日志目录"
        : _link.SessionDirectory is not null
            ? $"打开当前会话的日志目录：{path}"
            : $"打开上次会话的日志目录：{path}";

    /// <summary>
    /// 面板标题。超出条数上限被挤掉过老记录就要在这里说一句——
    /// 面板上只剩 5000 条而会话跑了两万条，不写清楚就是「记录凭空少了」，属于交互正确性。
    /// </summary>
    public string RecordsTitle => _link.Transcript.DiscardedRecords > 0
        ? $"通信记录（已达 {_link.Transcript.RecordCap} 条上限，丢掉最早的 {_link.Transcript.DiscardedRecords} 条）"
        : "通信记录";

    public LinkPhase Phase
    {
        get => _phase;
        private set
        {
            if (SetProperty(ref _phase, value))
            {
                RaiseLinkStateNotifications();
            }
        }
    }

    public string PhaseText => Phase.Text();

    /// <summary>状态点：绿=已连接，红=对方断了，橙=正在连或重连，灰=没连。</summary>
    public Brush PhaseBrush => Phase switch
    {
        LinkPhase.Online => OnlineBrush,
        LinkPhase.PeerLost => PeerLostBrush,
        LinkPhase.Offline or LinkPhase.Disconnecting => OfflineBrush,
        _ => BusyBrush
    };

    public string ConnectButtonText => Phase.IsLocked() ? "断开" : "连接";

    /// <summary>断开时「连接」是顶栏唯一的主动作，用强调色；连上后这颗变成「断开」，退回描边。</summary>
    public bool ShowsConnectAccent => !Phase.IsLocked();

    /// <summary>连上以后配置只读，避免边跑边改端口和定时器。</summary>
    public bool AreParametersEditable => !Phase.IsLocked() && !_isLinkBusy;

    public CommunicationRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (ReferenceEquals(_selectedRecord, value))
            {
                return;
            }

            if (_selectedRecord is not null)
            {
                _selectedRecord.PropertyChanged -= OnSelectedRecordChanged;
            }

            _detailLoad?.Cancel();
            _detailLoad = null;
            _selectedRecord = value;
            _detailExpanded = false;
            if (value is not null)
            {
                value.PropertyChanged += OnSelectedRecordChanged;
            }

            OnPropertyChanged();
            RecordDetail = value is null ? "选中一条记录看详情" : value.DetailText;
        }
    }

    private string _recordDetail = "选中一条记录看详情";
    private bool _detailExpanded;
    private CancellationTokenSource? _detailLoad;

    public string RecordDetail
    {
        get => _recordDetail;
        private set => SetProperty(ref _recordDetail, value);
    }

    /// <summary>
    /// 大消息正文默认折叠，双击详情区展开或收起。展开时从 sml.log 分段读，
    /// 每读一页就刷新一次，界面跟着往下滚；正文太大的时候不会一次性塞进内存外的控件。
    /// </summary>
    public async Task ToggleDetailExpandAsync()
    {
        if (_selectedRecord is not { } record || record.Sml is not null)
        {
            return;
        }

        _detailLoad?.Cancel();
        _detailLoad = null;

        var header = $"时间     {record.TimeText}{Environment.NewLine}{Environment.NewLine}";

        if (_detailExpanded)
        {
            _detailExpanded = false;
            RecordDetail = record.DetailText;
            return;
        }

        if (record.SmlSequence is not { } sequence || _link.Log is not { } log)
        {
            RecordDetail = header + "这次会话的日志已经关闭，正文读不出来了。";
            return;
        }

        if (!log.TryGetSmlLocation(sequence, out var location))
        {
            RecordDetail = header + "正文还在写盘，稍后再展开。";
            return;
        }

        _detailExpanded = true;
        var cancellation = (_detailLoad = new CancellationTokenSource()).Token;
        RecordDetail = header + "正在读取 sml.log…";
        try
        {
            // 只往界面里灌这么多：10MB 的 B 展开成 SML 有三十兆，TextBox 装不下也画不动。
            var builder = new StringBuilder(header);
            await using var reader = await log.OpenBlockReaderAsync(location, cancellation);
            while (builder.Length < DetailCharacterCap)
            {
                var page = await reader.ReadPageAsync(cancellation);
                if (page is null)
                {
                    break;
                }

                // 这一页可能是在选中已经换掉之后才读回来的。不先看一眼令牌，
                // 上一条记录的正文就会盖到新选中的那条头上。
                cancellation.ThrowIfCancellationRequested();
                builder.Append(page);
                RecordDetail = builder.ToString();
            }

            if (builder.Length >= DetailCharacterCap)
            {
                RecordDetail = builder.Append(
                    $"{Environment.NewLine}…… 这里只显示前 {DetailCharacterCap / 1024} KB，" +
                    $"完整正文在 {log.SmlLogPath}").ToString();
            }
        }
        catch (OperationCanceledException)
        {
            // 换了选中或收起：这次读取作废，新的选中会重写详情。
        }
        catch (IOException exception)
        {
            RecordDetail = header + $"读 sml.log 失败：{exception.Message}";
        }
    }

    public EndpointRole Role
    {
        get => _role;
        set => SetEditorProperty(ref _role, value);
    }

    public ConnectionMode Mode
    {
        get => _mode;
        set => SetEditorProperty(ref _mode, value);
    }

    public TransportKind Transport
    {
        get => _transport;
        set
        {
            if (SetEditorProperty(ref _transport, value))
            {
                OnPropertyChanged(nameof(HsmsSettingsVisibility));
                OnPropertyChanged(nameof(Secs1SettingsVisibility));
            }
        }
    }

    /// <summary>参数面板按传输方式分成两套，同一时刻只露一套。</summary>
    public Visibility HsmsSettingsVisibility =>
        _transport == TransportKind.Hsms ? Visibility.Visible : Visibility.Collapsed;

    public Visibility Secs1SettingsVisibility =>
        _transport == TransportKind.Secs1 ? Visibility.Visible : Visibility.Collapsed;

    public string IpAddress
    {
        get => _ipAddress;
        set => SetEditorProperty(ref _ipAddress, value);
    }

    public int Port
    {
        get => _port;
        set => SetEditorProperty(ref _port, value);
    }

    public string SerialPortName
    {
        get => _serialPortName;
        set => SetEditorProperty(ref _serialPortName, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetEditorProperty(ref _baudRate, value);
    }

    public int DataBits
    {
        get => _dataBits;
        set => SetEditorProperty(ref _dataBits, value);
    }

    public SerialParity Parity
    {
        get => _parity;
        set => SetEditorProperty(ref _parity, value);
    }

    public SerialStopBits StopBits
    {
        get => _stopBits;
        set => SetEditorProperty(ref _stopBits, value);
    }

    public SerialFlowControl FlowControl
    {
        get => _flowControl;
        set => SetEditorProperty(ref _flowControl, value);
    }

    public int DeviceId
    {
        get => _deviceId;
        set => SetEditorProperty(ref _deviceId, value);
    }

    public int T1Seconds
    {
        get => _t1Seconds;
        set => SetEditorProperty(ref _t1Seconds, value);
    }

    public int T2Seconds
    {
        get => _t2Seconds;
        set => SetEditorProperty(ref _t2Seconds, value);
    }

    public int T3Seconds
    {
        get => _t3Seconds;
        set => SetEditorProperty(ref _t3Seconds, value);
    }

    public int T4Seconds
    {
        get => _t4Seconds;
        set => SetEditorProperty(ref _t4Seconds, value);
    }

    public int T5Seconds
    {
        get => _t5Seconds;
        set => SetEditorProperty(ref _t5Seconds, value);
    }

    public int T6Seconds
    {
        get => _t6Seconds;
        set => SetEditorProperty(ref _t6Seconds, value);
    }

    public int T7Seconds
    {
        get => _t7Seconds;
        set => SetEditorProperty(ref _t7Seconds, value);
    }

    public int T8Seconds
    {
        get => _t8Seconds;
        set => SetEditorProperty(ref _t8Seconds, value);
    }

    public bool LinkTestEnabled
    {
        get => _linkTestEnabled;
        set => SetEditorProperty(ref _linkTestEnabled, value);
    }

    public int LinkTestIntervalSeconds
    {
        get => _linkTestIntervalSeconds;
        set => SetEditorProperty(ref _linkTestIntervalSeconds, value);
    }

    /// <summary>顶栏上一眼能看到当前要连哪儿；详细参数在「连接参数」面板里改。</summary>
    public string ConfigurationSummary => _transport == TransportKind.Secs1
        ? $"{Role} · SECS-I {SerialPortName} {BaudRate} · Dev {DeviceId} · T3 {T3Seconds}s"
        : $"{Role} · HSMS {Mode} · {IpAddress}:{Port} · Dev {DeviceId} · T3 {T3Seconds}s";

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            // 写进任何一句新的话，都算上一条错误已经被读掉了，红色跟着退。
            _statusIsError = false;
            SetProperty(ref _statusMessage, value);
            OnPropertyChanged(nameof(StatusIsError));
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    /// <summary>状态栏这句是不是在报错。</summary>
    public bool StatusIsError => _statusIsError;

    /// <summary>状态栏前景色：平时和 XAML 的 MutedBrush 一致，报错转红。</summary>
    public Brush StatusBrush => _statusIsError ? StatusErrorBrush : StatusMutedBrush;

    /// <summary>
    /// 启动阶段的一次性交代，比如老目录改名结果。
    /// 只能在 InitializeAsync 之后调，否则会被初始化那条状态盖掉。
    /// </summary>
    public void ShowStartupNotice(string notice) => StatusMessage = notice;

    /// <summary>
    /// 关窗口时程序要落盘再断开，这期间整窗是禁用的。
    /// 不写一句，用户看到的就是一秒钟的灰窗，和卡死分不出来。
    /// </summary>
    public void ShowShutdownNotice() => StatusMessage = "正在保存日志并断开连接，稍等…";

    /// <summary>
    /// 失败要说清楚，而且不能被随后到来的连接状态变化冲掉——
    /// 报错之后紧接着一次 PeerLost 或 Retrying，原因就没了，只剩一句无关的话。
    /// </summary>
    private void Fail(string message)
    {
        StatusMessage = message;
        _statusIsError = true;
        OnPropertyChanged(nameof(StatusIsError));
        OnPropertyChanged(nameof(StatusBrush));
    }

    public string LibraryHeader
    {
        // 只报条数：左栏被右侧「文件 + 新消息」挤得只剩一条窄轨。
        // 窗口拖到最窄（860 DIP）时标题只有约 64 DIP，「消息库 29 条」和带「·」的写法
        // 都会被裁成「消息库 …」；这个写法实测 55 DIP，两种宽度下都能完整显示。
        get => $"消息库 {_library.Messages.Count}";
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshMessages();
            }
        }
    }

    public MessageEntry? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (_refreshingMessages && value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedMessage, value))
            {
                OnPropertyChanged(nameof(HasSelectedMessage));
                SyncMessageEditor();
                RefreshStructure();
                SendCommand.RaiseCanExecuteChanged();
                DeleteMessageCommand.RaiseCanExecuteChanged();
                DuplicateMessageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedMessage => SelectedMessage is not null;

    public string MessageName
    {
        get => _messageName;
        set
        {
            if (SetProperty(ref _messageName, value) && !_loadingMessageEditor)
            {
                CommitMessageName();
            }
        }
    }

    public string MessageDescription
    {
        get => _messageDescription;
        set
        {
            if (SetProperty(ref _messageDescription, value) && !_loadingMessageEditor)
            {
                CommitDescription();
            }
        }
    }

    public string StreamText
    {
        get => _streamText;
        set
        {
            if (SetProperty(ref _streamText, value) && !_loadingMessageEditor)
            {
                CommitHeader();
            }
        }
    }

    public string FunctionText
    {
        get => _functionText;
        set
        {
            if (SetProperty(ref _functionText, value) && !_loadingMessageEditor)
            {
                CommitHeader();
            }
        }
    }

    public bool MessageWBit
    {
        get => _messageWBit;
        set
        {
            if (SetProperty(ref _messageWBit, value) && !_loadingMessageEditor)
            {
                CommitHeader();
            }
        }
    }

    /// <summary>下拉框选中的一项。存进库的还是名字（TargetName），界面上看的是 Display。</summary>
    public AutoReplyOption MessageAutoReply
    {
        get => _messageAutoReply;
        set
        {
            // 换候选列表时 ComboBox 会先把选中项清成 null，那一下不是用户操作，不能拿去改写库。
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _messageAutoReply, value) && !_loadingMessageEditor)
            {
                CommitAutoReply();
            }
        }
    }

    public IReadOnlyList<AutoReplyOption> AutoReplyCandidates => _autoReplyCandidates;

    /// <summary>字段标题带上本条的 SxF（和 W），用户不用回左栏去核对这条是几号。</summary>
    /// <summary>
    /// 标签只写字段名。带上「本条 S1F2 W」在中栏 3* 的宽度里会被截成「本条 S1F…」，
    /// 而 SxFy 和 W 就在旁边那几格，标签里再写一遍只是挤占下拉框的宽度。
    /// </summary>
    public string AutoReplyFieldLabel => "自动回复";

    public bool HasMessages => Messages.Count > 0;

    public string EmptyHint => _library.Messages.Count == 0
        ? "消息库为空\n用「文件」菜单导入 .sxml"
        : "没有匹配的消息";

    /// <summary>
    /// 读那份唯一的连接参数。文件不存在或内容不可用时退回默认值，原因写在状态栏里，
    /// 程序照常能用：用户直接在面板上改 IP 和端口就行，不需要先有一个"配置"。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _settingsStore.LoadAsync(cancellationToken);

        _profile = loaded.Profile;
        LoadEditor(_profile);

        // 参数文件读不出来、内容坏了或参数越界，程序会退回默认值继续用。
        // 这是要让人看见的事，标红；但不拦住使用。
        if (loaded.Problem is { } problem)
        {
            Fail(problem);
        }
        else
        {
            StatusMessage = "连接参数已就绪，点「连接」开始。";
        }
    }

    public async Task FlushAsync()
    {
        CancelScheduledSave();
        await SaveCurrentProfileAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        CancelScheduledSave();
        CancelStructureRefresh();
    }

    /// <summary>关窗口时先落盘再断开，端口要还给系统。</summary>
    public async Task ShutdownAsync()
    {
        await FlushAsync();

        // 连接跑在线程池上，关窗口可能正好撞上「正在打开串口」。
        // 不等它结束就断开，会在连接刚建好之后把端口和日志丢给一次已经跑完的收尾。
        if (_connectInFlight is { } connecting)
        {
            try
            {
                await connecting;
            }
            catch (Exception)
            {
                // 连接失败的原因由 ConnectAsync 那边写状态栏；这里只是等它结束。
            }
        }

        await DisconnectAsync();
    }

    private bool CanToggleConnection => !_isLinkBusy;

    private async Task ToggleConnectionAsync()
    {
        if (_link.CanConnect)
        {
            await ConnectAsync();
            return;
        }

        await DisconnectAsync();
    }

    private async Task ConnectAsync()
    {
        SetLinkBusy(true);
        try
        {
            CancelScheduledSave();
            if (!TryBuildProfile(out var profile, out var error))
            {
                Fail(error);
                return;
            }

            // 存不下也要连：数据目录只读不该拦住用户测机台，参数照样抄进会话目录。
            await SaveProfileAsync(profile);

            // 新连接从「不筛选」开始：否则重连后可能一条都看不见，像没收到消息。
            ClearRecordFilter(announce: false);
            Records.Clear();
            _visibleRecordCount = 0;
            OnPropertyChanged(nameof(RecordsHint));
            OnPropertyChanged(nameof(RecordsTitle));

            // 这句要在 Connect 之前写：SECS-I 打开串口是同步的，连上以后阶段事件会把
            // 「已连接」先写上去，事后再写「正在打开」就把它盖掉了。
            StatusMessage = profile.Transport == TransportKind.Secs1
                ? $"正在打开串口 {profile.SerialPortName}（{profile.BaudRate}，{profile.DataBits} 位数据）。"
                : $"正在连接 {profile.IpAddress}:{profile.Port}（{profile.Mode}）。";

            var connecting = Task.Run(() => _link.Connect(profile));
            _connectInFlight = connecting;
            try
            {
                // 串口打开是同步调用，碰上不听话的驱动会把界面卡住；放到线程池上等，
                // 上面那句「正在打开串口」才真能画出来。
                await connecting;
            }
            catch (InvalidOperationException exception)
            {
                Fail(exception.Message);
                return;
            }
            finally
            {
                if (ReferenceEquals(_connectInFlight, connecting))
                {
                    _connectInFlight = null;
                }
            }

            // 丢弃计数是 Core 在 Connect 里清的，这里再报一次面板标题才不会留旧数字。
            OnPropertyChanged(nameof(RecordsTitle));
        }
        finally
        {
            SetLinkBusy(false);
        }
    }

    private async Task DisconnectAsync()
    {
        SetLinkBusy(true);
        try
        {
            await _link.DisconnectAsync();
        }
        finally
        {
            SetLinkBusy(false);
            StatusMessage = "已断开。";
        }
    }

    private void OnLinkPhaseChanged(object? sender, LinkPhase phase)
    {
        OnUiThread(() =>
        {
            Phase = phase;

            // 刚报过错就别盖掉：连接状态胶囊自己会用颜色和文字说清楚现在是什么阶段，
            // 状态栏留着那句失败原因更有用，否则用户只看到一句无关的话，错在哪查不到。
            if (_statusIsError)
            {
                return;
            }

            // 胶囊变了，旁边那句状态栏也得跟着变，不然连上以后还一直写着「正在连接」。
            // Offline 不在这里说话：断开是命令发起的，那边已经写了「已断开。」，别盖掉。
            StatusMessage = phase switch
            {
                LinkPhase.Online => $"已连接 {_link.Profile?.Label}。",
                LinkPhase.PeerLost when _link.Profile?.Transport == TransportKind.Secs1 =>
                    "串口无数据：对端断开或端口拔出。点「断开」后再重连。",
                LinkPhase.PeerLost => "对方已断开；本端仍在等待，可点「断开」后重连。",
                LinkPhase.Retrying => "还没连上，正在按 T5 重试。",
                _ => StatusMessage
            };
        });
    }

    private bool CanSend =>
        Phase == LinkPhase.Online && SelectedMessage is not null && !_isLinkBusy && !SmlPreviewHasError;

    /// <summary>结构里的值转不成 SECS 数据时不发，只把原因写进状态栏。</summary>
    private async Task SendMessageAsync()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        if (!SmlConverter.TryToMessage(entry, out var message, out var error))
        {
            Fail(error);
            return;
        }

        // 带 W 的消息在途时间是 T3，最长能配到一天。这段时间不写出「正在等、可以取消」，
        // 发送按键变灰就又被读成程序卡死，这句提示不能省。
        SetSendWaiting(true);
        StatusMessage = entry.WBit
            ? $"等待 {entry.Signature} 回复（T3 {_t3Seconds} 秒），可取消。"
            : $"正在发送 {entry.Signature}。";

        try
        {
            var outcome = await _link.SendAsync(message);

            // 发出去了和没发出去（T3 超时、发送失败）都是这一句返回值，靠开头那个词分开。
            if (outcome.StartsWith(ConnectionLink.SentPrefix, StringComparison.Ordinal))
            {
                StatusMessage = outcome;
            }
            else
            {
                Fail(outcome);
            }
        }
        catch (InvalidOperationException exception)
        {
            Fail(exception.Message);
        }
        finally
        {
            SetSendWaiting(false);
            message.Dispose();
        }
    }

    /// <summary>
    /// 「取消等待」按下去之后消息已经在线上了，取消的只是等回复这件事，
    /// 所以说法要说清结果，不能只写「已取消」让人以为没发出去。
    /// Core 没在等（回复正好到了）时不改写状态栏那一句结果。
    /// </summary>
    private void CancelPendingWait()
    {
        // 按下去马上给一句反馈，Core 那边「已发出…已取消等待回复」随后覆盖它。
        // 返回 false 是回复正好到了，那一句话已经在路上，这里不抢着改写。
        if (_link.CancelPendingSend())
        {
            StatusMessage = "已取消等待回复。";
        }
    }

    private void SetSendWaiting(bool value)
    {
        if (_isSendWaiting == value)
        {
            return;
        }

        _isSendWaiting = value;
        OnPropertyChanged(nameof(CancelSendVisibility));
        CancelSendCommand.RaiseCanExecuteChanged();
    }

    /// <summary>按钮只在真有等待时出现；跟命令的可用条件用同一个字段，不另找一遍状态。</summary>
    public Visibility CancelSendVisibility => _isSendWaiting ? Visibility.Visible : Visibility.Collapsed;

    private void OnRecordAdded(CommunicationRecord record)
    {
        OnUiThread(() =>
        {
            Records.Add(record);
            if (RecordMatchesFilter(record))
            {
                _visibleRecordCount++;
            }

            OnPropertyChanged(nameof(RecordsHint));
        });
    }

    /// <summary>
    /// Core 挤出上限之外的老记录时才会走到这里。面板上少一条是看得见的变化，
    /// 所以标题里那句「丢掉最早的 N 条」要跟着一起刷新，不能只删列表。
    /// </summary>
    private void OnRecordRemoved(CommunicationRecord record)
    {
        OnUiThread(() =>
        {
            Records.Remove(record);
            if (RecordMatchesFilter(record))
            {
                _visibleRecordCount--;
            }

            OnPropertyChanged(nameof(RecordsHint));
            OnPropertyChanged(nameof(RecordsTitle));
        });
    }

    /// <summary>没开筛选时任何一条都算「可见」，计数在两种状态下走同一套加减。</summary>
    private bool RecordMatchesFilter(CommunicationRecord record) =>
        _filterStream is not { } stream
        || (record.Header.Stream == stream
            && (_filterFunction is not { } function || record.Header.Function == function));

    /// <summary>
    /// 只换列表视图，不动 <see cref="Records"/>：筛选期间到达的消息照样进集合，
    /// 取消筛选时全部回来，不会缺一段。
    /// </summary>
    private void ApplyRecordFilter()
    {
        var text = RecordFilterText.Trim();
        if (!MessageLibrary.TryParseSignatureOrStream(text, out var stream, out var function))
        {
            RecordFilterHasError = true;
            Fail("筛选写法不对，例如 S6 或 S6F11。");
            return;
        }

        RecordFilterHasError = false;
        _filterStream = stream;
        _filterFunction = function;
        RecordFilterText = function is { } f ? $"S{stream}F{f}" : $"S{stream}";
        _recordsView.Filter = item => item is CommunicationRecord record && RecordMatchesFilter(record);
        _recordsView.Refresh();
        _visibleRecordCount = Records.Count(RecordMatchesFilter);
        OnPropertyChanged(nameof(IsRecordFilterActive));
        OnPropertyChanged(nameof(ActiveRecordFilterText));
        OnPropertyChanged(nameof(RecordsHint));
        ClearRecordFilterCommand.RaiseCanExecuteChanged();
        StatusMessage = _visibleRecordCount == 0
            ? $"筛选已开启：只看 {ActiveRecordFilterText}，当前没有匹配记录（共 {Records.Count} 条）。"
            : $"筛选已开启：只看 {ActiveRecordFilterText}（共 {Records.Count} 条，显示 {_visibleRecordCount} 条）。";
    }

    /// <summary>
    /// 取消筛选时把框里的字也清掉，否则框里还写着 S6F11、列表却已经全回来了，看着像没生效。
    /// 新连接前也走这里，只是不报状态栏那句话。
    /// </summary>
    private void ClearRecordFilter(bool announce)
    {
        var wasActive = IsRecordFilterActive;
        RecordFilterHasError = false;
        _filterStream = null;
        _filterFunction = null;
        _recordsView.Filter = null;
        _recordsView.Refresh();
        RecordFilterText = string.Empty;
        _visibleRecordCount = Records.Count;
        if (wasActive)
        {
            OnPropertyChanged(nameof(IsRecordFilterActive));
            OnPropertyChanged(nameof(ActiveRecordFilterText));
            OnPropertyChanged(nameof(RecordsHint));
        }

        ClearRecordFilterCommand.RaiseCanExecuteChanged();
        if (announce && wasActive)
        {
            StatusMessage = $"已取消筛选，恢复显示 {Records.Count} 条记录。";
        }
    }

    /// <summary>
    /// 打开当前或上一次会话的日志目录。目录被外部清掉时不弹窗，只在状态栏说清楚，
    /// 免得一个 Explorer 失败把整窗挡住。
    /// </summary>
    private void OpenLogFolder()
    {
        if (LogFolderPath is not { } path)
        {
            Fail("还没有会话日志。先连接一次再查看。");
            return;
        }

        if (!Directory.Exists(path))
        {
            Fail($"日志目录不存在：{path}。可能已被清理。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            StatusMessage = "已在资源管理器中打开日志目录。";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Fail($"打不开日志目录：{exception.Message}");
        }
    }

    /// <summary>
    /// 记录的状态和正文长度是在收发线程上改的，这个处理器也就在那个线程上跑。
    /// 详情文本会牵动 MainWindow 里的滚动逻辑（直接碰 TextBox），必须回到 UI 线程再改，
    /// 否则抛 InvalidOperationException，异常还会顺着收发回调把连接带崩。
    /// </summary>
    private void OnSelectedRecordChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(CommunicationRecord.Status) or nameof(CommunicationRecord.SmlByteLength))
            || sender is not CommunicationRecord record)
        {
            return;
        }

        OnUiThread(() =>
        {
            // 排队期间选中可能已经换掉或者正文已经展开，别把旧的详情盖上去。
            if (_detailExpanded || !ReferenceEquals(_selectedRecord, record))
            {
                return;
            }

            RecordDetail = record.DetailText;
        });
    }

    /// <summary>
    /// Secs4Net 的收发回调在它自己的线程上，集合和绑定只能回到 UI 线程改。
    /// 用 BeginInvoke 不用 Invoke：Invoke 会把 UI 线程里的异常原样抛回收发回调，
    /// 而回调里冒出去的异常会直接把 HSMS 连接带崩；顺便也不会被卡住的 UI 线程拖住收消息。
    /// </summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color?)ColorConverter.ConvertFromString(hex) ?? Colors.Gray);
        brush.Freeze();
        return brush;
    }

    /// <summary>所有异步命令共用一个异常出口：写状态栏，不要让进程消失。</summary>
    private AsyncRelayCommand AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(execute, canExecute, OnCommandFailed);

    private void OnCommandFailed(Exception exception) =>
        Fail($"操作失败（{exception.GetType().Name}）：{exception.Message}");

    private void SetLinkBusy(bool value)
    {
        _isLinkBusy = value;
        RaiseLinkStateNotifications();
    }

    private void RaiseLinkStateNotifications()
    {
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(PhaseBrush));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ShowsConnectAccent));
        OnPropertyChanged(nameof(AreParametersEditable));
        ConnectCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        // 连接一开就有会话目录了，断开后还要留在上次那份上；
        // 阶段和忙碌状态变的时候一并刷新按钮和提示。
        OnPropertyChanged(nameof(LogFolderPath));
        OnPropertyChanged(nameof(LogFolderHint));
        OpenLogFolderCommand.RaiseCanExecuteChanged();
    }

    private async Task ImportLibraryAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入消息库",
            Filter = "SecsExpress 消息库 (*.sxml)|*.sxml|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        // 这次会话还没导入过东西时，把对话框停在样本库所在的数据目录
        // （启动时从程序里解出来的那份和 settings.json 并排）。
        // 导入过了就按系统记住的位置走，别每次都跳回来。
        if (_libraryPath.Length == 0)
        {
            var samples = new AppDataPaths(AppContext.BaseDirectory).SampleDirectory;
            if (Directory.Exists(samples))
            {
                dialog.InitialDirectory = samples;
            }
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportFromPathAsync(dialog.FileName);
    }

    /// <summary>
    /// 导入的两条路径共用这一段：换成新库、清掉搜索、刷新列表和状态栏。
    /// 返回成功与否，拖放那条要在成功之后才追加「只用了第一个文件」那句。
    /// </summary>
    private async Task<bool> ImportFromPathAsync(string path, string? note = null)
    {
        try
        {
            var library = await LibraryFile.LoadAsync(path);
            _library = library;
            _libraryPath = path;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            RefreshMessages();
            // 换库就是把整份文档换掉，字典条目跟着整体重取。
            RefreshDataEntries();
            RefreshEntryTree();
            var entriesNote = library.DataEntries.Count == 0
                ? string.Empty
                : $"、{library.DataEntries.Count} 个字典条目";
            StatusMessage = $"已导入 {library.Messages.Count} 条消息{entriesNote}：{Path.GetFileName(path)}{note}";
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException or FormatException)
        {
            Fail($"导入失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 从资源管理器把文件拖到左栏。空库直接导入；库里有消息先弹确认再整体替换。
    /// 「文件」菜单导入不走这里，它不弹框，两条路径反应不一样是有意的。
    /// 后缀不对的直接拒掉，不去读文件内容。一次拖多个只取第一个，并在状态栏说清用了哪个。
    /// </summary>
    public async Task ImportDroppedFileAsync(string path, int droppedCount)
    {
        var fileName = Path.GetFileName(path);
        if (!LibraryFile.HasLibraryExtension(path))
        {
            Fail($"拖入的不是 .sxml 或 .xml 文件：{fileName}");
            return;
        }

        var count = _library.Messages.Count;
        if (count > 0)
        {
            // 拖放比点菜单容易误触，所以这一路加确认；默认焦点落在「否」，和清空、删除同一防线口径。
            var answer = MessageBox.Show(
                $"消息库里有 {count} 条消息，导入 {fileName} 会全部替换。{Environment.NewLine}没有导出过的改动会丢失。",
                "替换消息库",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "已取消，消息库不变。";
                return;
            }
        }

        await ImportFromPathAsync(path, droppedCount > 1 ? $"（拖入 {droppedCount} 个文件，只用了第一个）" : null);
    }

    private async Task ExportLibraryAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出消息库",
            Filter = "SecsExpress 消息库 (*.sxml)|*.sxml",
            DefaultExt = ".sxml",
            AddExtension = true,
            FileName = _libraryPath.Length == 0 ? "消息库.sxml" : Path.GetFileName(_libraryPath)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await LibraryFile.SaveAsync(_library, dialog.FileName);
            _libraryPath = dialog.FileName;
            OnPropertyChanged(nameof(LibraryHeader));
            StatusMessage = $"已导出 {_library.Messages.Count} 条消息到 {dialog.FileName}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail($"导出失败：{exception.Message}");
        }
    }

    private void ClearLibrary()
    {
        // 清空是整份文档没了，字典条目跟着一起走，所以确认框里要把两样都报出来：
        // 只有条目、没有消息的库也算「有东西可清」，不能提前当空库放过。
        var parts = new List<string>();
        if (_library.Messages.Count > 0)
        {
            parts.Add($"{_library.Messages.Count} 条消息");
        }

        if (_library.DataEntries.Count > 0)
        {
            parts.Add($"{_library.DataEntries.Count} 个字典条目");
        }

        if (parts.Count == 0)
        {
            StatusMessage = "消息库已经是空的。";
            return;
        }

        var summary = string.Join("、", parts);

        // 防误触：默认焦点落在「否」上，回车不会直接清空。
        var answer = MessageBox.Show(
            $"确定要清空消息库里的 {summary} 吗？没有导出过的改动会全部丢失。",
            "清空消息库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _library.Clear();
        _libraryPath = string.Empty;
        RefreshMessages();
        RefreshDataEntries();
        RefreshEntryTree();
        StatusMessage = $"已清空消息库（原有 {summary}）。";
    }

    /// <summary>
    /// 整库扫一遍 AutoReply，只报不改。状态栏不标红：检查跑完了，不是程序出错；
    /// 问题逐条写在报告里，断指仍走改名/删除那条置空口径，不在这里顺手改文本。
    /// </summary>
    private void CheckAutoReply()
    {
        var report = AutoReplyAudit.Run(_library);
        var count = _library.Messages.Count;

        if (!report.HasIssues)
        {
            StatusMessage = $"自动回复检查：{count} 条消息，未发现问题。";
            MessageBox.Show(
                $"检查了 {count} 条消息，自动回复配置一致。",
                "自动回复检查",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var lines = new List<string>
        {
            $"检查了 {count} 条消息，发现 {report.Issues.Count} 处问题"
            + $"（错误 {report.ErrorCount}、警告 {report.WarningCount}）。",
            string.Empty
        };
        lines.AddRange(report.Issues.Select(issue =>
            $"[{SeverityLabel(issue.Severity)}] {MessageLabel(issue.Message)}：{issue.Text}"));

        StatusMessage = $"自动回复检查：{count} 条消息，发现 {report.Issues.Count} 处问题"
            + $"（错误 {report.ErrorCount}、警告 {report.WarningCount}）。";
        MessageBox.Show(
            string.Join(Environment.NewLine, lines),
            "自动回复检查",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string SeverityLabel(AutoReplyIssueSeverity severity) =>
        severity == AutoReplyIssueSeverity.Error ? "错误" : "警告";

    private static string MessageLabel(MessageEntry entry) =>
        entry.Name.Length == 0 ? entry.Signature : $"{entry.Signature} · {entry.Name}";

    private void NewMessage()
    {
        try
        {
            // 名字自动取一个没被占用的：连着按两次「新建」就是「NewMessage」「NewMessage 2」。
            // 消息名必须唯一（SxFy 可以重复，名字不行），撞名不再是报错的理由。
            // 默认名用英文，和消息名本身的命名习惯一致。
            var entry = _library.NewMessage(1, 1, _library.NextFreeMessageName("NewMessage"));
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            RefreshMessages();
            SelectedMessage = Messages.FirstOrDefault(message => message.Index == entry.Index);
            StatusMessage = $"已新建 {entry.Signature}「{entry.Name}」。";
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException or ArgumentException)
        {
            Fail($"新建消息失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 复制选中的那条，副本紧跟在原条后面。除名字以外全部照抄，名字一律换成「原名 Copy」，
    /// 撞了就依次试「原名 Copy 2」——后缀用英文是跟着消息名的命名习惯走的。
    /// 名字必须和原条不同、也必须非空：自动回复按 Name 指向消息，两条同名之后指向落到谁身上
    /// 变成看库里的顺序。SxFy 相同不要紧，同一 S2F41 可以有 START、STOP、PP_SELECT 三条。
    /// 不弹确认：这一步无损，撤销这套工具本来也不做。
    /// </summary>
    private void DuplicateMessage()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        var baseName = entry.Name.Length == 0 ? "Copy" : $"{entry.Name} Copy";
        MessageEntry copy;
        try
        {
            copy = _library.DuplicateMessage(entry, _library.NextFreeMessageName(baseName));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Fail($"复制失败：{exception.Message}");
            return;
        }

        // RefreshMessages 会按 Index 把选中留在原条上，这里要显式挪到副本：
        // 副本的名字就地可改，选中留在原条就等于让用户自己去列表里找那一行。
        RefreshMessages();
        SelectedMessage = Messages.FirstOrDefault(message => message.Index == copy.Index);
        StatusMessage = $"已复制为 {copy.Name}。";
    }

    /// <summary>
    /// 删掉选中的那条消息。没有撤销（用户 2026-10-18 定），确认框是唯一防线：默认焦点落在「否」上。
    /// 正文只说两件必须说的事：不可恢复、有几条自动回复指向它。
    /// 指着它的引用由 Core 在删除时一并置空（不是只提示不改），
    /// 所以确认框说的是「删完之后会被置空」，不是「留着但不再命中」。
    /// </summary>
    private void DeleteMessage()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        var label = entry.Name.Length == 0 ? entry.Signature : $"{entry.Name}（{entry.Signature}）";

        // 数指向它的自动回复改用 Core 那一条口径（和 FindTarget 取目标是同一比法），
        // 界面不自己数第二遍：数法不一致的话提醒说没有、实际已经断了。
        // 空名字的消息不可能被指向，Core 那边空目标直接给空列表。
        var brokenReferences = AutoReplyResolver.FindReferrers(_library, entry.Name, entry);

        var lines = new List<string> { $"确定删除 {label}？不可恢复。" };
        if (brokenReferences.Count > 0)
        {
            lines.Add($"{brokenReferences.Count} 条自动回复指向它，删完之后这些引用会被置空。");
        }

        var answer = MessageBox.Show(
            string.Join(Environment.NewLine, lines),
            "删除消息",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _library.RemoveMessage(entry);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Fail($"删除失败：{exception.Message}");
            return;
        }

        // 删完之后 SelectedMessage 手上那条已经不在库里了，但它的 Index 正好落到原来那条的下一条上，
        // RefreshMessages 按 Index 找回选中，效果就是选中往下走一格；删的是最后一条时选中变空。
        RefreshMessages();
        StatusMessage = brokenReferences.Count == 0
            ? $"已删除 {label}。"
            : $"已删除 {label}，{brokenReferences.Count} 条自动回复已置空。";
    }

    /// <summary>
    /// 搜索框每敲一个字都会走到这里，所以要按 Index 把当前选中的消息找回来，
    /// 否则过滤一次选中就跳回第一条，中栏正在编辑的内容会莫名其妙换掉。
    /// </summary>
    private void RefreshMessages()
    {
        var selected = SelectedMessage?.Index;
        var entries = _library.Query(SearchText);

        // Clear 会让 ListBox 反推一个 null 上来，中栏跟着清空一次：
        // 树重建、编辑条被禁用、正在输入的框失去焦点。改个名字闪一下不值得，
        // 所以重建期间先把这个 null 挡掉，等列表填好了再决定选中谁。
        _refreshingMessages = true;
        try
        {
            Messages.Clear();
            foreach (var entry in entries)
            {
                Messages.Add(entry);
            }
        }
        finally
        {
            _refreshingMessages = false;
        }

        var next = selected is null
            ? null
            : entries.FirstOrDefault(entry => entry.Index == selected);
        if (!ReferenceEquals(next, _selectedMessage))
        {
            if (Equals(next, _selectedMessage))
            {
                // 值一样但快照是新的：说明 XML 改过而消息头没变（例如粘贴同一条 SxFy 的 SML），
                // 记录相等判断看不出来，得手动重刷一遍，顺便把新实例推回 ListBox
                // （Clear 已经把它的选中项抹掉了，不推一次列表上就没有高亮）。
                _selectedMessage = next;
                OnPropertyChanged(nameof(SelectedMessage));
                OnPropertyChanged(nameof(HasSelectedMessage));
                SyncMessageEditor();
                RefreshStructure();
            }
            else
            {
                SelectedMessage = next;
            }
        }

        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(LibraryHeader));
    }

    private void AddChild(SecsItemType type)
    {
        // 空结构没有可选中的节点，加的是这条消息的根元素：Core 的 AddChild 对空 <Structure>
        // 就是这个语义，提示语让点的也是这一步，不能在这里断掉。
        // 字典那一页不套用这一步：条目之间互不隶属，没选中条目就没有落点，不能替人挑一条。
        var parent = SelectedNode?.Element
            ?? (EditorTab == DictionaryTabIndex ? null : SelectedMessage?.Structure);
        if (parent is null)
        {
            return;
        }

        var isRoot = SelectedNode is null;
        var added = StructureEditor.AddChild(parent, type);
        if (added is null)
        {
            Fail(isRoot ? "这条消息已经有消息体了。" : "这个位置不能加子项。");
            return;
        }

        RefreshTreeOf(added);
        var addedNode = FindNode(added);
        if (addedNode?.Parent is { } parentNode)
        {
            parentNode.IsExpanded = true;
        }

        OnNodeSelected(addedNode);
        StatusMessage = isRoot
            ? $"已添加根节点 {SecsItemTypes.ElementName(type)}。"
            : $"已添加子项 {SecsItemTypes.ElementName(type)}。";
    }

    private void AddSibling(SecsItemType type)
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        // 条目那一层的「同级」是另一条条目，归字典那条排版路管，不走结构树那一步。
        if (IsEntryNode(node) && node.Parent is null && FindDataEntry(node) is { } currentEntry)
        {
            CreateEntry(type, currentEntry);
            return;
        }

        var added = StructureEditor.AddSibling(node.Element, type);
        if (added is null)
        {
            Fail("这个位置不能加同级。");
            return;
        }

        RefreshTreeOf(added);
        var addedNode = FindNode(added);
        if (addedNode?.Parent is { } parent)
        {
            parent.IsExpanded = true;
        }

        OnNodeSelected(addedNode);
        StatusMessage = $"已添加同级 {SecsItemTypes.ElementName(type)}。";
    }

    private void DeleteNode()
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        // 字典那一层的节点就是条目本身，删它要走 RemoveEntry：连它前面那段换行缩进一起删，
        // 才不留成片空行给同事比对。StructureEditor.Delete 只管元素，不懂这一段排版。
        if (IsEntryNode(node) && node.Parent is null)
        {
            DeleteEntry();
            return;
        }

        // 段要在删之前问：元素一脱离文档就落到 Elsewhere，那棵树该刷哪一棵就再也问不出来了。
        var isEntry = IsEntryNode(node);
        var parentElement = node.Element.Parent;
        if (!StructureEditor.Delete(node.Element))
        {
            Fail("这个节点删不掉。");
            return;
        }

        if (isEntry)
        {
            RefreshEntryTree();
        }
        else
        {
            RefreshStructure();
        }

        if (parentElement is not null && FindNode(parentElement) is { } parentNode)
        {
            OnNodeSelected(parentNode);
        }

        StatusMessage = $"已删除 {node.TypeLabel} 节点。";
    }

    private void CopyNode()
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        _structureClipboard = StructureEditor.CloneSubtree(node.Element);
        StatusMessage = $"已复制 {node.TypeLabel} 节点。";
        RaiseTreeCommandCanExecute();
    }

    /// <summary>
    /// 粘贴落在哪：选中的节点；结构页没选中就是这条消息的结构，那时收下的可能是根。
    /// 字典页没选中条目时不给落点——条目互不隶属，粘到「字典根」等于凭空多出一条，
    /// 那件事该由「新增数据」或「存入字典」明着做，不由 Ctrl+V 顺手做。
    /// </summary>
    private XElement? PasteTarget() => SelectedNode?.Element
        ?? (EditorTab == DictionaryTabIndex ? null : SelectedMessage?.Structure);

    private bool CanPasteNode()
    {
        if (_structureClipboard is null)
        {
            return false;
        }

        var target = PasteTarget();
        return target is not null && StructureEditor.PlacementOf(target) != PastePlacement.Nowhere;
    }

    private void PasteNode()
    {
        var source = _structureClipboard;
        var target = PasteTarget();
        if (source is null || target is null)
        {
            return;
        }

        var placement = StructureEditor.PlacementOf(target);
        if (placement == PastePlacement.Nowhere)
        {
            Fail("这个位置不能粘贴：它既不收子项，也没有同级位置。");
            return;
        }

        // 每次粘的都是槽里这一份的克隆：槽本身必须一直不挂树，否则粘第二次
        // 插进去的就是同一个实例，Core 会当成「搬活节点」拦下。
        var pasted = StructureEditor.PasteInto(StructureEditor.CloneSubtree(source), target);
        if (pasted is null)
        {
            Fail("这个位置不能粘贴。");
            return;
        }

        RefreshTreeOf(pasted);
        var pastedNode = FindNode(pasted);
        if (pastedNode?.Parent is { } parent)
        {
            parent.IsExpanded = true;
        }

        OnNodeSelected(pastedNode);
        StatusMessage = placement switch
        {
            PastePlacement.AsChild when target.Name.LocalName == "Structure"
                => $"已粘贴为这条消息的根节点 {pasted.Name.LocalName}。",
            PastePlacement.AsChild => $"已粘贴为子项 {pasted.Name.LocalName}。",
            _ => $"已粘贴为同级 {pasted.Name.LocalName}。"
        };
    }

    /// <summary>
    /// 拖动时这个落点收不收、以什么方式收（搬走还是复制一份）。光标形状和放下去那一步都问它，
    /// 界面那层不再判第二遍。zone 是目标行的上/中/下三段。
    /// </summary>
    public DropWay NodeDropWay(StructureNodeViewModel dragged, StructureNodeViewModel target, DropZone zone) =>
        StructureEditor.DropWayOf(dragged.Element, target.Element, zone);

    /// <summary>把 dragged 放到 target 的上段、中段或下段。搬还是复制由 Core 判，界面只看它给的答案。</summary>
    public void DropNode(StructureNodeViewModel dragged, StructureNodeViewModel target, DropZone zone)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        ArgumentNullException.ThrowIfNull(target);

        switch (StructureEditor.DropWayOf(dragged.Element, target.Element, zone))
        {
            case DropWay.Move:
                MoveNode(dragged, target, zone);
                return;
            case DropWay.Copy:
                CopyNodeInto(dragged, target, zone);
                return;
            default:
                Fail(DropRefusalReason(dragged.Element, target.Element, zone));
                return;
        }
    }

    /// <summary>
    /// 字典页跨条目那一种落点：在落点插一份克隆，来源那一条不动。
    /// 状态栏要说清这是复制，不然人会以为原来那条被挪走了。
    /// </summary>
    private void CopyNodeInto(StructureNodeViewModel dragged, StructureNodeViewModel target, DropZone zone)
    {
        // 落点要在动之前问：插完之后 target 那一档可能就变了，照插完的状态会说错话。
        var placement = StructureEditor.DropPlacementOf(dragged.Element, target.Element, zone);
        var copy = StructureEditor.CopyForDrop(dragged.Element, target.Element, zone);
        if (copy is null)
        {
            Fail("这个位置不收：复制一份也插不进去。");
            return;
        }

        RefreshTreeOf(copy);
        SelectDropped(FindNode(copy));
        StatusMessage = placement switch
        {
            PastePlacement.AsChild =>
                $"已把 {dragged.TypeLabel} 复制一份放进 {target.TypeLabel} 里，原来那一条没动。",
            PastePlacement.AsSiblingBefore =>
                $"已把 {dragged.TypeLabel} 复制一份插到 {target.TypeLabel} 前面，原来那一条没动。",
            _ => $"已把 {dragged.TypeLabel} 复制一份排到 {target.TypeLabel} 后面，原来那一条没动。"
        };
    }

    /// <summary>把 dragged 搬到 target 的上段、中段或下段。落点不收时树不动，状态栏说清为什么。</summary>
    private void MoveNode(StructureNodeViewModel dragged, StructureNodeViewModel target, DropZone zone)
    {
        // 落点要在搬之前问：搬完之后 target 那一档可能就变了（往空 <Structure> 里放一个节点，
        // 它就不再接收了），照搬完的状态会说错话。
        var placement = StructureEditor.DropPlacementOf(dragged.Element, target.Element, zone);
        if (!StructureEditor.Move(dragged.Element, target.Element, zone))
        {
            Fail(DropRefusalReason(dragged.Element, target.Element, zone));
            return;
        }

        RefreshTreeOf(dragged.Element);
        SelectDropped(FindNode(dragged.Element));
        StatusMessage = placement switch
        {
            PastePlacement.AsChild => $"已把 {dragged.TypeLabel} 放进 {target.TypeLabel} 里。",
            PastePlacement.AsSiblingBefore => $"已把 {dragged.TypeLabel} 移到 {target.TypeLabel} 前面。",
            _ => $"已把 {dragged.TypeLabel} 移到 {target.TypeLabel} 后面。"
        };
    }

    /// <summary>放下之后选中新位置那一行，父项顺手展开，不然复制出来的那份藏在折叠的节点里看不见。</summary>
    private void SelectDropped(StructureNodeViewModel? droppedNode)
    {
        if (droppedNode?.Parent is { } parent)
        {
            parent.IsExpanded = true;
        }

        OnNodeSelected(droppedNode);
    }

    /// <summary>
    /// 拒绝原因。判断顺序照 Core 的 DropPlacementOf：先能不能动，再是不是自己身上，
    /// 再是不是自己的子树，然后是不是跨段，最后才是这个位置两档都不收。
    /// 上段和下段还要多一句：根元素没有同级位置。
    /// </summary>
    private static string DropRefusalReason(XElement dragged, XElement target, DropZone zone)
    {
        if (!StructureEditor.CanMove(dragged))
        {
            return "这个节点不能拖：根元素只收东西，值和 Maps 不是能搬的元素。";
        }

        if (ReferenceEquals(dragged, target))
        {
            return "不能放在自己身上。";
        }

        for (var parent = target.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, dragged))
            {
                return "不能把它放进自己的子树里。";
            }
        }

        if (SecsSections.Of(dragged) != SecsSections.Of(target))
        {
            return "消息结构和数据字典之间不能拖，用右键的复制和粘贴。";
        }

        if (zone != DropZone.Middle && !StructureEditor.CanAddSibling(target))
        {
            return zone == DropZone.Top
                ? "它上面没有同级位置：根元素不能再加兄弟。"
                : "它下面没有同级位置：根元素不能再加兄弟。";
        }

        return "这个位置不收：它既不能当父项，也没有同级位置。";
    }

    /// <summary>
    /// 用粘贴的一条完整 SML 覆盖当前消息的消息头和结构，Name、AutoReply 等其余内容保留。
    /// 解析失败只报状态不动消息，粘错的文本留在输入框里方便改。
    /// </summary>
    private void ApplyPastedSml()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        if (!SmlParser.TryParse(PasteSmlText, out var parsed, out var error) || parsed is null)
        {
            Fail($"SML 解析失败：{error}");
            return;
        }

        var updated = _library.ApplySml(entry, parsed);
        PasteSmlText = string.Empty;
        RefreshMessages();
        StatusMessage = $"已用粘贴的 SML 覆盖 {updated.Signature} 的消息头和结构。";
    }

    /// <summary>
    /// 改一个节点的名字或值都要重建整条消息的 SML 预览和校验。
    /// 实测 1MB 的 B 值一次要 40ms（预览）加 100ms（校验），还分配 17MB；
    /// 边打字边重建界面就跟不上手了，所以停下来一会儿再刷一次。
    /// </summary>
    /// <summary>
    /// 进就地编辑。同时只承认一行在编辑，否则两行的框都在、改的却是同一个 SelectedNode，
    /// 看着就是「明明点的是这一行，写进去的是另一行」。
    /// </summary>
    internal void BeginStructureEdit(StructureNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_editingNode is { } previous && !ReferenceEquals(previous, node))
        {
            previous.IsEditing = false;
        }

        _editingNode = node;
        node.IsEditing = true;
    }

    /// <summary>
    /// 把行内编辑收掉。名称和值是边敲边写进 XML 的，这里没有要提交的东西，只把「正在编辑」
    /// 这个状态交出去。点到正在编辑那一行以外（树的空白处、工具栏、别的页签）走这一条——
    /// 之前只有「选中挪到别的行」和「整棵树重建」两条路会收框，点空白处不改选中，
    /// 那个框就一直开着。行内按回车也走这一条，返回值让调用方知道确实是收了一个框。
    /// </summary>
    internal bool EndStructureEdit()
    {
        if (_editingNode is not { } editing)
        {
            return false;
        }

        // B 类的负数到这一下才转十六进制：逐字输入时转换会把刚敲的 '-' 当成完整值。
        editing.CommitValue();
        editing.IsEditing = false;
        _editingNode = null;
        return true;
    }

    /// <summary>
    /// 行上填完一个字：延迟重算 SML 预览和校验。改的是字典里的条目时，条目快照也换一份——
    /// DataEntry 是构造时拍的照片，不会自己跟着 XML 走，不刷名字框就还显示改之前那个值。
    /// </summary>
    internal void OnStructureEdited(StructureNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        CancelStructureRefresh();
        var cancellation = (_structureRefresh = new CancellationTokenSource()).Token;
        _ = RaiseStructureNotificationsAfterDelayAsync(cancellation);

        if (IsEntryNode(node))
        {
            RefreshDataEntries();
        }
    }

    private async Task RaiseStructureNotificationsAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StructureRefreshDelayMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RaiseStructureNotifications();
    }

    private void CancelStructureRefresh()
    {
        var cancellation = Interlocked.Exchange(ref _structureRefresh, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void RaiseStructureNotifications()
    {
        OnPropertyChanged(nameof(SmlPreviewText));
        OnPropertyChanged(nameof(SmlPreviewHasError));
        SendCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 树整体重建而不是增量改：StructureNode 构造时就把子节点拍成快照，
    /// 增量同步要自己维护两套树的对应关系，重建只要按 XElement 身份找回展开和选中状态。
    /// </summary>
    private void RefreshStructure()
    {
        // 这里马上就会重新算一遍预览，打字时挂着的那次延迟刷新可以撤掉。
        CancelStructureRefresh();

        var collapsed = _structureNodes
            .Where(node => !node.IsExpanded)
            .Select(node => node.Element)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        // 这里问的是结构那一轨，不能问 SelectedNode：后者跟着页签走，人停在字典页时
        // 换一条消息也会走到这里，拿到的会是字典条目，塞回结构轨之后所有结构命令
        // 都会作用在字典元素上（条目被加子项、结构树看起来「点不动」）。
        var selected = _selectedStructureNode?.Element;

        _structureNodes.Clear();
        StructureRootItems.Clear();
        // 这一棵的旧 VM 全部作废，编辑态跟着结束（新 VM 的 IsEditing 默认就是 false）。
        // 正在编辑的要是字典那棵树上的行，不能顺手一起清：字典树没重建，那一行的框还开着，
        // 而 _editingNode 一清空就没人再去关它，再双击另一行变成两行同时编辑
        // （1597 行那条「同时只承认一行在编辑」的规矩就是这么破的）。
        if (_editingNode is { } editing && !IsEntryNode(editing))
        {
            _editingNode = null;
        }

        var root = SelectedMessage is null ? null : StructureNode.FromStructure(SelectedMessage.Structure);
        if (root is not null)
        {
            StructureRootItems.Add(new StructureNodeViewModel(root, null, this));
        }

        foreach (var node in _structureNodes)
        {
            node.IsExpanded = !collapsed.Contains(node.Element);
        }

        if (selected is null || FindNode(selected) is not { } restored)
        {
            ClearStructureSelection();
        }
        else
        {
            SetSelectedNode(ref _selectedStructureNode, restored);
        }

        OnPropertyChanged(nameof(StructureHint));
        RaiseStructureNotifications();
        RaiseTreeCommandCanExecute();
    }

    /// <summary>把选中消息的属性抄到编辑条上。带 guard，避免加载又反过来触发提交。</summary>
    private void SyncMessageEditor()
    {
        _loadingMessageEditor = true;
        try
        {
            var entry = SelectedMessage;
            MessageName = entry?.Name ?? string.Empty;
            MessageDescription = entry?.Description ?? string.Empty;
            StreamText = entry?.Stream.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            FunctionText = entry?.Function.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            MessageWBit = entry?.WBit ?? false;
            _autoReplyCandidates = AutoReplyOptions.Build(_library, entry);
            OnPropertyChanged(nameof(AutoReplyCandidates));
            OnPropertyChanged(nameof(AutoReplyFieldLabel));
            MessageAutoReply = AutoReplyOptions.Find(_autoReplyCandidates, entry?.AutoReply);
        }
        finally
        {
            _loadingMessageEditor = false;
        }
    }

    private void CommitMessageName()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        var name = MessageName.Trim();
        if (name == entry.Name)
        {
            // 只多敲了空格：把输入框显示回真正存下来的名字。
            OnPropertyChanged(nameof(MessageName));
            return;
        }

        try
        {
            var oldName = entry.Name;
            // 指着旧名的引用会被 SetName 一并置空，所以名单必须在改之前取，改完再数就是 0 了。
            var cleared = AutoReplyResolver.FindReferrers(_library, oldName);
            var updated = _library.SetName(entry, name);
            RefreshAfterMessageEdit(updated.Index);
            StatusMessage = $"已把 {updated.Signature} 的名称改成「{updated.Name}」。{ClearedAutoReplyNote(cleared, oldName)}";
        }
        catch (ArgumentException exception)
        {
            // 空名字或者撞了别的消息：名字非空且唯一是 SecsExpress 对消息体的硬要求，
            // 写进去同事那边打不开，所以这里拒掉并把框里的字改回存下来的那一个。
            Fail($"改名失败：{exception.Message}");
            SyncMessageEditor();
        }
    }

    private void CommitDescription()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        var description = MessageDescription.Trim();
        if (description == entry.Description)
        {
            // 只多了首尾空格：把框里的字改回存下来的那一份。
            OnPropertyChanged(nameof(MessageDescription));
            return;
        }

        var updated = _library.SetDescription(entry, description);
        RefreshAfterMessageEdit(updated.Index);
        StatusMessage = description.Length == 0
            ? $"已清空 {updated.Signature} 的描述。"
            : $"已更新 {updated.Signature} 的描述。";
    }

    /// <summary>
    /// 改名会把指着旧名的自动回复置空（Core 里做的），置空了几条、是哪几条得当场说一句：
    /// 不然用户只看到目标名字变了，不知道自己顺手关掉了别人的回复。
    /// 状态栏那句带省略号和 ToolTip，名单长了悬停能看全。
    /// </summary>
    private static string ClearedAutoReplyNote(IReadOnlyList<MessageEntry> cleared, string oldName)
    {
        if (cleared.Count == 0)
        {
            return string.Empty;
        }

        var who = string.Join("、", cleared.Select(entry => $"{entry.Signature} {entry.Name}"));
        return $"另有 {cleared.Count} 条自动回复原来指着「{oldName}」，已置空：{who}。";
    }

    private void CommitHeader()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        if (!TryParseHeaderField(StreamText, out var stream) || !TryParseHeaderField(FunctionText, out var function))
        {
            Fail("S 和 F 必须是 1 到 255 之间的整数，已改回原值。");
            SyncMessageEditor();
            return;
        }

        if (stream == entry.Stream && function == entry.Function && MessageWBit == entry.WBit)
        {
            SyncMessageEditor();
            return;
        }

        try
        {
            var updated = _library.SetHeader(entry, stream, function, MessageWBit);
            RefreshAfterMessageEdit(updated.Index);
            StatusMessage = $"已把消息头改成 {updated.Signature}{(updated.WBit ? " W" : string.Empty)}。";
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Fail($"改消息头失败：{exception.Message}");
            SyncMessageEditor();
        }
    }

    private void CommitAutoReply()
    {
        var entry = SelectedMessage;
        if (entry is null)
        {
            return;
        }

        var target = MessageAutoReply.TargetName;
        if (target == entry.AutoReply)
        {
            return;
        }

        var updated = _library.SetAutoReply(entry, target);
        RefreshAfterMessageEdit(updated.Index);
        StatusMessage = target is null
            ? $"已取消 {updated.Signature} 的自动回复。"
            : $"已把 {updated.Signature} 的自动回复设成「{MessageAutoReply.Display}」。";
    }

    /// <summary>
    /// 鼠标按下时提交消息头，提交原本会立刻重建左栏；重建发生在 ListBox 处理这次点击之前，
    /// 被点的行会被换掉，点击就丢了。先把重建压到这次输入事件处理完之后再做。
    /// </summary>
    internal void DeferMessageListRefresh() => _deferMessageListRefresh = true;

    /// <summary>把推迟的重建补上；没有待办就只把开关落回去。</summary>
    internal void FlushDeferredMessageListRefresh()
    {
        _deferMessageListRefresh = false;
        if (_deferredMessageIndex is not { } index)
        {
            return;
        }

        _deferredMessageIndex = null;
        RefreshAfterMessageEditCore(index);
    }

    /// <summary>
    /// MessageEntry 是一次性快照，改完属性必须重建列表，左栏的名字和 SxFy 才会跟着变。
    /// 改名或改头可能让这条消息不再匹配搜索词——用户正盯着它编辑，
    /// 让它从列表里消失太莫名，所以清掉搜索词再找回来。
    /// </summary>
    private void RefreshAfterMessageEdit(int index)
    {
        if (_deferMessageListRefresh)
        {
            _deferredMessageIndex = index;
            return;
        }

        RefreshAfterMessageEditCore(index);
    }

    private void RefreshAfterMessageEditCore(int index)
    {
        RefreshMessages();
        if (SelectedMessage is not null)
        {
            return;
        }

        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        RefreshMessages();
        SelectedMessage = Messages.FirstOrDefault(message => message.Index == index);
    }

    private static bool TryParseHeaderField(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && value is >= 1 and <= 255;

    /// <summary>
    /// 行 VM 造出来就按自己所在的段登记：字典段的进条目那一本，其余进消息结构那一本。
    /// 两本分开，重建一棵树才不会把另一棵的展开和选中一起丢掉。
    /// </summary>
    internal void RegisterNode(StructureNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var registry = IsEntryNode(node) ? _entryNodes : _structureNodes;
        registry.Add(node);
    }

    private StructureNodeViewModel? FindNode(XElement element)
    {
        var registry = SecsSections.Of(element) == SecsSection.Dictionary ? _entryNodes : _structureNodes;
        return registry.FirstOrDefault(node => ReferenceEquals(node.Element, element));
    }

    /// <summary>
    /// 命令只改了其中一棵树，就只重建那一棵。
    /// 字典这棵不能直接重建：条目 VM 包的是 <see cref="DataEntry"/> 那份快照，
    /// 命令改的是活文档里的 XElement，快照不重取的话「添加子项」加进去的那一层
    /// 在树上根本不会出现；所以先重取条目再重建。
    /// </summary>
    private void RefreshTreeOf(XElement element)
    {
        if (SecsSections.Of(element) == SecsSection.Dictionary)
        {
            // 先让库把条目清单按文档重拍，再往界面上抄：拖动改的是顶层条目的先后次序，
            // 不重拍这一步，XML 换了序、字典页还是老序。
            _library.SyncDataEntries();
            RefreshDataEntries();
            RefreshEntryTree();
        }
        else
        {
            RefreshStructure();
        }
    }

    /// <summary>
    /// 「添加子项」能不能点。判断必须和 AddChild 里算落点那一句是同一条，
    /// 否则就是「菜单亮着、点下去没反应」。空结构那一步只归结构页。
    /// </summary>
    private bool CanAddChildToSelection() => SelectedNode is { } node
        ? StructureEditor.CanAddChild(node.Element)
        : EditorTab != DictionaryTabIndex
            && SelectedMessage?.Structure is { } structure && StructureEditor.CanAddChild(structure);

    private void RaiseTreeCommandCanExecute()
    {
        AddChildCommand.RaiseCanExecuteChanged();
        AddSiblingCommand.RaiseCanExecuteChanged();
        DeleteNodeCommand.RaiseCanExecuteChanged();
        CopyNodeCommand.RaiseCanExecuteChanged();
        PasteNodeCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasAddChildTarget));
        OnPropertyChanged(nameof(HasAddSiblingTarget));
        // 「存入字典」和条目那一侧的「删除」看的是同一个选中项，跟着树命令一起刷，不另找时机。
        RaiseEntryCommandCanExecute();
    }

    /// <summary>
    /// 工具栏那两个带下级的菜单能不能展开。没有落点时菜单里的 15 个类型全是灰的，
    /// 不如整行灰掉，省得点开一屏灰菜单再猜为什么。
    /// </summary>
    public bool HasAddChildTarget => CanAddChildToSelection();

    public bool HasAddSiblingTarget => SelectedNode is not null
        && StructureEditor.CanAddSibling(SelectedNode.Element);

    // ---------- 数据字典条目：单独存起来的 data 和 list ----------

    private DataEntry? _selectedDataEntry;
    private string _dataEntryName = string.Empty;
    private bool _loadingDataEntry;
    private bool _refreshingEntries;

    public ObservableCollection<DataEntry> DataEntries { get; } = [];

    public RelayCommand SaveNodeAsEntryCommand { get; }

    public RelayCommand<SecsItemType> NewEntryCommand { get; }

    public RelayCommand DeleteEntryCommand { get; }

    public DataEntry? SelectedDataEntry
    {
        get => _selectedDataEntry;
        set
        {
            // 列表重建时控件会反推一个 null 上来，那一次不算用户取消选中，和消息列表同一条处理。
            if (_refreshingEntries && value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedDataEntry, value))
            {
                OnPropertyChanged(nameof(HasSelectedDataEntry));
                SyncDataEntryEditor();
                RaiseEntryCommandCanExecute();
            }
        }
    }

    public bool HasSelectedDataEntry => SelectedDataEntry is not null;

    /// <summary>
    /// 选中条目的名字，就地改。留空是把 Name 属性去掉——数据元素的名字可空也可重（用户给的
    /// SecsExpress 实测，树里字典里同一档）。只有拿旧快照改名这类 Core 拒的情况才把框里的字改回去。
    /// </summary>
    public string DataEntryName
    {
        get => _dataEntryName;
        set
        {
            if (SetProperty(ref _dataEntryName, value) && !_loadingDataEntry)
            {
                CommitEntryName();
            }
        }
    }

    /// <summary>
    /// 条目列表整体重建：DataEntry 是活节点的一次性快照，存、改、删之后手上那份都可能作废，
    /// 所以和左栏消息列表用同一套做法，不做增量同步。
    /// keepIndex 是给「刚存进去的那一条要选中」用的。
    /// </summary>
    private void RefreshDataEntries(int? keepIndex = null)
    {
        var selected = keepIndex ?? SelectedDataEntry?.Index;
        _refreshingEntries = true;
        try
        {
            DataEntries.Clear();
            foreach (var entry in _library.DataEntries)
            {
                DataEntries.Add(entry);
            }
        }
        finally
        {
            _refreshingEntries = false;
        }

        SelectedDataEntry = selected is null ? null : DataEntries.FirstOrDefault(entry => entry.Index == selected);
        OnPropertyChanged(nameof(EntryHint));
        RaiseEntryCommandCanExecute();
    }

    /// <summary>
    /// 条目树整体重建，和结构树那一条同一个做法：按 XElement 身份找回展开和选中，不做增量同步。
    /// 条目里改过结构之后必须刷，否则 DataEntry 那份快照还是旧的，名字框会把改之前的值显示回去。
    /// </summary>
    private void RefreshEntryTree()
    {
        var collapsed = _entryNodes
            .Where(node => !node.IsExpanded)
            .Select(node => node.Element)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var selected = _selectedEntryNode?.Element;

        _entryNodes.Clear();
        EntryRootItems.Clear();

        // 条目正在就地编辑时重建，那一行的框就没了；结构页那棵树没动，它的编辑态要留着。
        if (_editingNode is { } editing && IsEntryNode(editing))
        {
            _editingNode = null;
        }

        foreach (var entry in DataEntries)
        {
            // 从活元素重新拍一份，不能用 entry.Node：那份是条目解析时的快照，
            // 之后在字典里加的子项、改的值都不在里面——旧写法就是这么让树上一个字没变的。
            EntryRootItems.Add(new StructureNodeViewModel(new StructureNode(entry.Element), null, this));
        }

        foreach (var node in _entryNodes)
        {
            node.IsExpanded = !collapsed.Contains(node.Element);
        }

        if (selected is not null && FindNode(selected) is { } restored)
        {
            SetSelectedNode(ref _selectedEntryNode, restored);
        }
        else
        {
            ClearEntrySelection();
        }

        SyncSelectedEntryFromTree();
        OnPropertyChanged(nameof(EntryHint));
        RaiseTreeCommandCanExecute();
    }

    /// <summary>字典页没条目时的提示语，和结构页那条同一个用法。</summary>
    public string EntryHint => DataEntries.Count == 0
        ? "无条目；点「新增数据」新建，或在结构页将节点存入字典"
        : string.Empty;

    /// <summary>
    /// 名字框和「删除条目」要的是条目本身，可人可能选在条目里面的某一节上，
    /// 所以顺着父链走到顶层那一节点，再按元素身份认回它是哪一条条目。
    /// </summary>
    private void SyncSelectedEntryFromTree()
    {
        var root = _selectedEntryNode;
        while (root?.Parent is { } parent)
        {
            root = parent;
        }

        SelectedDataEntry = root is null
            ? null
            : DataEntries.FirstOrDefault(entry => ReferenceEquals(entry.Element, root.Element));
    }

    /// <summary>把名字框填成选中条目那一个，带 guard，免得同步反过来又触发一次改名。</summary>
    private void SyncDataEntryEditor()
    {
        _loadingDataEntry = true;
        try
        {
            DataEntryName = SelectedDataEntry?.Name ?? string.Empty;
        }
        finally
        {
            _loadingDataEntry = false;
        }
    }

    /// <summary>
    /// 把结构页选中的那个节点存成条目。名字就取节点自己的名字，没有名字也照样存——
    /// 数据元素的名字可空可重（SecsExpress 实测），不是主键，也不值得为它编一个。
    /// 一律追加、重名也另起一条、不弹确认：条目和消息之间又是复制关系，所以存一次不可能改坏哪条消息。
    /// </summary>
    private void SaveNodeAsEntry()
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        DataEntry entry;
        try
        {
            entry = _library.SaveEntry(node.DisplayName, node.Element);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Fail($"存入字典失败：{exception.Message}");
            return;
        }

        RefreshDataEntries(entry.Index);
        RefreshEntryTree();
        StatusMessage = entry.Name.Length == 0
            ? $"已把 {node.TypeLabel} 存入字典（没有名字）。"
            : $"已把 {node.TypeLabel} 存入字典「{entry.Name}」。";
    }

    private void CommitEntryName()
    {
        var entry = SelectedDataEntry;
        if (entry is null)
        {
            return;
        }

        var previous = entry.Name;
        var name = DataEntryName.Trim();
        if (name == previous)
        {
            // 只多敲了空格：把输入框显示回真正存下来的名字。
            OnPropertyChanged(nameof(DataEntryName));
            return;
        }

        try
        {
            var updated = _library.RenameEntry(entry, name);
            RefreshDataEntries(updated.Index);
            RefreshEntryTree();
            StatusMessage = updated.Name.Length == 0
                ? $"已把条目「{previous}」的名字清空。"
                : $"已把条目改成「{updated.Name}」。";
        }
        catch (ArgumentException exception)
        {
            Fail($"改名失败：{exception.Message}");
            SyncDataEntryEditor();
        }
    }

    /// <summary>
    /// 「新增数据」的类型菜单走这里，建在末尾。
    /// </summary>
    private void NewEntry(SecsItemType type) => CreateEntry(type, null);

    /// <summary>
    /// 建一条条目。建出来先不带名字：数据元素的名字可空也可重（SecsExpress 实测），
    /// 空着让人在行上填，比编一个「新条目」那种假名字干净。
    /// <paramref name="after"/> 是给「在某一条目下面添加同级」用的——条目那一层的同级就是另一条条目，
    /// 必须走 Core 的 NewEntry 那条排版路（它管着条目之间那格换行和 8 空格），
    /// 直接用 StructureEditor.AddSibling 会把两行挤成一行。
    /// </summary>
    private void CreateEntry(SecsItemType type, DataEntry? after)
    {
        DataEntry entry;
        try
        {
            entry = _library.NewEntry(string.Empty, type, after);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Fail($"新建条目失败：{exception.Message}");
            return;
        }

        RefreshDataEntries(entry.Index);
        RefreshEntryTree();
        if (FindNode(entry.Element) is { } node)
        {
            OnNodeSelected(node);
            BeginStructureEdit(node);
        }

        StatusMessage = after is null
            ? $"已在字典里新建 {SecsItemTypes.ElementName(type)} 条目，名字直接在行上填（可以留空）。"
            : $"已在「{after.Name}」后面新建 {SecsItemTypes.ElementName(type)} 条目。";
    }

    /// <summary>按顶层那一节点认条目：树里选中的可能是条目里面的某一节。</summary>
    private DataEntry? FindDataEntry(StructureNodeViewModel node)
    {
        var root = node;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        return DataEntries.FirstOrDefault(entry => ReferenceEquals(entry.Element, root.Element));
    }

    /// <summary>
    /// 删掉选中的条目。这里弹确认，和删树节点不弹是两档：树节点删了从条目再插一份就回来，
    /// 条目删了那份内容就没有下一个入口了。正文要说清已插入消息的那些副本不受影响，
    /// 否则用户会以为删条目能把消息里那一坨也一起带走。
    /// </summary>
    private void DeleteEntry()
    {
        var entry = SelectedDataEntry;
        if (entry is null)
        {
            return;
        }

        var label = entry.Name.Length == 0 ? "（没有名字的那一条）" : $"「{entry.Name}」";
        var answer = MessageBox.Show(
            $"确定删除条目{label}？不可恢复。已经复制进消息的那些内容不受影响。",
            "删除条目",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var index = entry.Index;
        try
        {
            _library.RemoveEntry(entry);
        }
        catch (ArgumentException exception)
        {
            Fail($"删除失败：{exception.Message}");
            RefreshDataEntries();
            RefreshEntryTree();
            return;
        }

        // 删完往下走一格，和删消息同一行为；删的是最后一条时选中变空。
        RefreshDataEntries(index);
        RefreshEntryTree();
        StatusMessage = $"已删除条目{label}。";
    }

    private void RaiseEntryCommandCanExecute()
    {
        SaveNodeAsEntryCommand.RaiseCanExecuteChanged();
        DeleteEntryCommand.RaiseCanExecuteChanged();
    }

    private void LoadEditor(ConnectionProfile profile)
    {
        _loadingEditor = true;
        try
        {
            Role = profile.Role;
            Mode = profile.Mode;
            Transport = profile.Transport;
            IpAddress = profile.IpAddress;
            Port = profile.Port;
            SerialPortName = profile.SerialPortName;
            BaudRate = profile.BaudRate;
            DataBits = profile.DataBits;
            Parity = profile.Parity;
            StopBits = profile.StopBits;
            FlowControl = profile.FlowControl;
            DeviceId = profile.DeviceId;
            T1Seconds = profile.T1Seconds;
            T2Seconds = profile.T2Seconds;
            T3Seconds = profile.T3Seconds;
            T4Seconds = profile.T4Seconds;
            T5Seconds = profile.T5Seconds;
            T6Seconds = profile.T6Seconds;
            T7Seconds = profile.T7Seconds;
            T8Seconds = profile.T8Seconds;
            LinkTestEnabled = profile.LinkTestEnabled;
            LinkTestIntervalSeconds = profile.LinkTestIntervalSeconds;
        }
        finally
        {
            _loadingEditor = false;
            OnPropertyChanged(nameof(ConfigurationSummary));
        }
    }

    private bool SetEditorProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        OnPropertyChanged(nameof(ConfigurationSummary));
        if (!_loadingEditor)
        {
            ScheduleSave();
        }

        return true;
    }

    private void ScheduleSave()
    {
        CancelScheduledSave();
        _saveDebounce = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_saveDebounce.Token);
    }

    private void CancelScheduledSave()
    {
        var cancellation = Interlocked.Exchange(ref _saveDebounce, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            await SaveCurrentProfileAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveCurrentProfileAsync(CancellationToken cancellationToken)
    {
        if (!TryBuildProfile(out var profile, out var error))
        {
            Fail(error);
            return;
        }

        await SaveProfileAsync(profile, cancellationToken);
    }

    /// <summary>把面板上的编辑值合成一份参数并验一遍；连接和保存共用这一份口径。</summary>
    private bool TryBuildProfile(
        [NotNullWhen(true)] out ConnectionProfile? profile,
        out string error)
    {
        profile = _profile with
        {
            Role = Role,
            Mode = Mode,
            Transport = Transport,
            IpAddress = IpAddress,
            Port = Port,
            SerialPortName = SerialPortName,
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            FlowControl = FlowControl,
            DeviceId = DeviceId,
            T1Seconds = T1Seconds,
            T2Seconds = T2Seconds,
            T3Seconds = T3Seconds,
            T4Seconds = T4Seconds,
            T5Seconds = T5Seconds,
            T6Seconds = T6Seconds,
            T7Seconds = T7Seconds,
            T8Seconds = T8Seconds,
            LinkTestEnabled = LinkTestEnabled,
            LinkTestIntervalSeconds = LinkTestIntervalSeconds
        };

        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            error = errors[0];
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task SaveProfileAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _saveGate.WaitAsync(cancellationToken);
            try
            {
                await _settingsStore.SaveAsync(profile, cancellationToken);
            }
            finally
            {
                _saveGate.Release();
            }

            _profile = profile;
            StatusMessage = $"连接参数已保存：{profile.Label}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail($"连接参数没能存下来：{exception.Message}");
        }
    }
}
