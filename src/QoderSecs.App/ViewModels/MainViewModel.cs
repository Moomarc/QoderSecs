using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using QoderSecs.App.Commands;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;

namespace QoderSecs.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>自动回复下拉框里代表「没有」的那一项。XML 里的表示是整个 AutoReply 元素不存在。</summary>
    private const string NoAutoReply = "（不自动回复）";

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
    private readonly HsmsLink _link = new();
    private bool _isLinkBusy;
    private LinkPhase _phase = LinkPhase.Offline;
    private CommunicationRecord? _selectedRecord;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _saveDebounce;
    private ConnectionProfile _profile = ConnectionProfile.CreateDefault();
    private bool _loadingEditor;
    private MessageLibrary _library = MessageLibrary.Empty;
    private string _libraryPath = string.Empty;
    private string _searchText = string.Empty;
    private MessageEntry? _selectedMessage;
    private readonly List<StructureNodeViewModel> _structureNodes = [];
    private StructureNodeViewModel? _selectedNode;
    private CancellationTokenSource? _structureRefresh;
    private string _pasteSmlText = string.Empty;
    private bool _loadingMessageEditor;
    private bool _refreshingMessages;
    private string _messageName = string.Empty;
    private string _streamText = string.Empty;
    private string _functionText = string.Empty;
    private bool _messageWBit;
    private string _messageAutoReply = NoAutoReply;
    private IReadOnlyList<string> _autoReplyOptions = [NoAutoReply];
    private EndpointRole _role;
    private ConnectionMode _mode;
    private string _ipAddress = string.Empty;
    private int _port;
    private int _deviceId;
    private int _t3Seconds;
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
        NewMessageCommand = AsyncCommand(() =>
        {
            NewMessage();
            return Task.CompletedTask;
        });
        AddChildCommand = new RelayCommand<SecsItemType>(
            AddChild,
            () => SelectedNode is not null && StructureEditor.CanAddChild(SelectedNode.Element));
        AddSiblingCommand = new RelayCommand<SecsItemType>(
            AddSibling,
            () => SelectedNode is not null && StructureEditor.CanAddSibling(SelectedNode.Element));
        DeleteNodeCommand = new RelayCommand(DeleteNode, () => SelectedNode is not null);
        ApplySmlCommand = new RelayCommand(
            ApplyPastedSml,
            () => SelectedMessage is not null && PasteSmlText.Trim().Length > 0);
        ConnectCommand = AsyncCommand(ToggleConnectionAsync, () => CanToggleConnection);
        SendCommand = AsyncCommand(SendMessageAsync, () => CanSend);
        _link.PhaseChanged += OnLinkPhaseChanged;
        _link.Transcript.RecordAdded += OnRecordAdded;

        // 载入文件时整份换掉库实例，所以给取值委托而不是存引用。
        _link.LibraryProvider = () => _library;
    }

    public ObservableCollection<MessageEntry> Messages { get; } = [];

    public IReadOnlyList<EndpointRole> EndpointRoles { get; } = Enum.GetValues<EndpointRole>();

    public IReadOnlyList<ConnectionMode> ConnectionModes { get; } = Enum.GetValues<ConnectionMode>();

    public AsyncRelayCommand CloseConfigPanelCommand { get; }

    public AsyncRelayCommand ImportLibraryCommand { get; }

    public AsyncRelayCommand ExportLibraryCommand { get; }

    public AsyncRelayCommand ClearLibraryCommand { get; }

    public AsyncRelayCommand NewMessageCommand { get; }

    public RelayCommand<SecsItemType> AddChildCommand { get; }

    public RelayCommand<SecsItemType> AddSiblingCommand { get; }

    public RelayCommand DeleteNodeCommand { get; }

    public RelayCommand ApplySmlCommand { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public IReadOnlyList<ItemTypeOption> ItemTypeOptions { get; } = ItemTypeOption.All;

    public ObservableCollection<StructureNodeViewModel> StructureRootItems { get; } = [];

    public StructureNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value))
            {
                return;
            }

            var previous = _selectedNode;
            _selectedNode = value;
            if (previous is not null)
            {
                previous.IsSelected = false;
            }

            if (value is not null)
            {
                value.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedNode));
            RaiseTreeCommandCanExecute();
        }
    }

    public bool HasSelectedNode => SelectedNode is not null;

    /// <summary>结构页签没树可显示时的提示语；有树时是空串，XAML 用它把提示隐藏掉。</summary>
    public string StructureHint => SelectedMessage is null
        ? "选择或新建消息后编辑结构"
        : StructureRootItems.Count == 0
            ? "这条消息没有消息体，用「＋ 子项」添加根节点"
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
                : $"这条消息现在转不成 SML，发送会被拦下：{error}";
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

    /// <summary>通信记录面板空着时的提示语；有记录时是空串，XAML 靠它把提示隐藏掉。</summary>
    public string RecordsHint => Records.Count == 0 ? "尚无通信记录" : string.Empty;

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

    public int DeviceId
    {
        get => _deviceId;
        set => SetEditorProperty(ref _deviceId, value);
    }

    public int T3Seconds
    {
        get => _t3Seconds;
        set => SetEditorProperty(ref _t3Seconds, value);
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
    public string ConfigurationSummary =>
        $"{Role} · {Mode} · {IpAddress}:{Port} · Dev {DeviceId} · T3 {T3Seconds}s";

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
        get
        {
            var source = _libraryPath.Length == 0 ? "消息库" : $"消息库 · {Path.GetFileName(_libraryPath)}";
            return $"{source}（{_library.Messages.Count} 条）";
        }
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

    public string MessageAutoReply
    {
        get => _messageAutoReply;
        set
        {
            if (SetProperty(ref _messageAutoReply, value) && !_loadingMessageEditor)
            {
                CommitAutoReply();
            }
        }
    }

    public IReadOnlyList<string> AutoReplyOptions => _autoReplyOptions;

    public bool HasMessages => Messages.Count > 0;

    public string EmptyHint => _library.Messages.Count == 0
        ? "消息库为空，用「文件 → 导入 .sxml…」打开一个"
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

            Records.Clear();
            OnPropertyChanged(nameof(RecordsHint));
            try
            {
                _link.Connect(profile);
            }
            catch (InvalidOperationException exception)
            {
                Fail(exception.Message);
                return;
            }

            StatusMessage = $"正在连接 {profile.IpAddress}:{profile.Port}（{profile.Mode}）。";
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
                LinkPhase.PeerLost => "对方已断开；本端还在等对方回来，等不及就点「断开」再重连。",
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

        try
        {
            var outcome = await _link.SendAsync(message);

            // 发出去了和没发出去（T3 超时、发送失败）都是这一句返回值，靠开头那个词分开。
            if (outcome.StartsWith(HsmsLink.SentPrefix, StringComparison.Ordinal))
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
            message.Dispose();
        }
    }

    private void OnRecordAdded(CommunicationRecord record)
    {
        OnUiThread(() =>
        {
            Records.Add(record);
            OnPropertyChanged(nameof(RecordsHint));
        });
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
        OnPropertyChanged(nameof(AreParametersEditable));
        ConnectCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
    }

    private async Task ImportLibraryAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入消息库",
            Filter = "SecsExpress 消息库 (*.sxml)|*.sxml|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        // 这次会话还没导入过东西时，把对话框停在样本库目录（启动时从程序里解出来的那份），
        // 用户不用自己去 exe 旁边翻。导入过了就按系统记住的位置走，别每次都跳回来。
        if (_libraryPath.Length == 0)
        {
            var samples = Path.Combine(AppContext.BaseDirectory, AppDataPaths.SampleSubdirectoryName);
            if (Directory.Exists(samples))
            {
                dialog.InitialDirectory = samples;
            }
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var library = await LibraryFile.LoadAsync(dialog.FileName);
            _library = library;
            _libraryPath = dialog.FileName;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            RefreshMessages();
            StatusMessage = $"已导入 {library.Messages.Count} 条消息：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException or FormatException)
        {
            Fail($"导入失败：{exception.Message}");
        }
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
        var count = _library.Messages.Count;
        if (count == 0)
        {
            StatusMessage = "消息库已经是空的。";
            return;
        }

        // 防误触：默认焦点落在「否」上，回车不会直接清空。
        var answer = MessageBox.Show(
            $"确定要清空消息库里的 {count} 条消息吗？没有导出过的改动会全部丢失。",
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
        StatusMessage = $"已清空消息库（原有 {count} 条消息）。";
    }

    private void NewMessage()
    {
        try
        {
            var entry = _library.NewMessage(1, 1, "新消息");
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            RefreshMessages();
            SelectedMessage = Messages.FirstOrDefault(message => message.Index == entry.Index);
            StatusMessage = $"已新建 {entry.Signature}「{entry.Name}」。";
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            Fail($"新建消息失败：{exception.Message}");
        }
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
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        var added = StructureEditor.AddChild(node.Element, type);
        if (added is null)
        {
            Fail("这个位置不能加子项。");
            return;
        }

        RefreshStructure();
        var addedNode = FindStructureNode(added);
        if (addedNode?.Parent is { } parent)
        {
            parent.IsExpanded = true;
        }

        SelectedNode = addedNode;
        StatusMessage = $"已添加子项 {SecsItemTypes.ElementName(type)}。";
    }

    private void AddSibling(SecsItemType type)
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        var added = StructureEditor.AddSibling(node.Element, type);
        if (added is null)
        {
            Fail("这个位置不能加同级。");
            return;
        }

        RefreshStructure();
        var addedNode = FindStructureNode(added);
        if (addedNode?.Parent is { } parent)
        {
            parent.IsExpanded = true;
        }

        SelectedNode = addedNode;
        StatusMessage = $"已添加同级 {SecsItemTypes.ElementName(type)}。";
    }

    private void DeleteNode()
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        var parentElement = node.Element.Parent;
        if (!StructureEditor.Delete(node.Element))
        {
            Fail("这个节点删不掉。");
            return;
        }

        RefreshStructure();
        SelectedNode = parentElement is null ? null : FindStructureNode(parentElement);
        StatusMessage = $"已删除 {node.TypeLabel} 节点。";
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
    internal void OnStructureEdited()
    {
        CancelStructureRefresh();
        var cancellation = (_structureRefresh = new CancellationTokenSource()).Token;
        _ = RaiseStructureNotificationsAfterDelayAsync(cancellation);
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
        var selected = SelectedNode?.Element;

        _structureNodes.Clear();
        StructureRootItems.Clear();

        var root = SelectedMessage is null ? null : StructureNode.FromStructure(SelectedMessage.Structure);
        if (root is not null)
        {
            StructureRootItems.Add(new StructureNodeViewModel(root, null, this));
        }

        foreach (var node in _structureNodes)
        {
            node.IsExpanded = !collapsed.Contains(node.Element);
        }

        SelectedNode = selected is null ? null : FindStructureNode(selected);
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
            StreamText = entry?.Stream.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            FunctionText = entry?.Function.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            MessageWBit = entry?.WBit ?? false;
            _autoReplyOptions = BuildAutoReplyOptions(entry);
            OnPropertyChanged(nameof(AutoReplyOptions));
            MessageAutoReply = string.IsNullOrWhiteSpace(entry?.AutoReply) ? NoAutoReply : entry!.AutoReply!;
        }
        finally
        {
            _loadingMessageEditor = false;
        }
    }

    /// <summary>
    /// 自动回复存的是另一条消息的 Name，所以下拉框列库里所有有名字的消息（不列自己）。
    /// 指向已经改名或删掉的消息时，也把那个名字列进去，
    /// 否则界面上显示空白，XML 里的值却被悄悄留着。
    /// </summary>
    private IReadOnlyList<string> BuildAutoReplyOptions(MessageEntry? entry)
    {
        var options = new List<string> { NoAutoReply };
        foreach (var message in _library.Query(null))
        {
            if (message.Index == entry?.Index || message.Name.Length == 0 || options.Contains(message.Name))
            {
                continue;
            }

            options.Add(message.Name);
        }

        var current = entry?.AutoReply?.Trim();
        if (current is { Length: > 0 } && !options.Contains(current))
        {
            options.Add(current);
        }

        return options;
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

        var updated = _library.SetName(entry, name);
        RefreshAfterMessageEdit(updated.Index);
        StatusMessage = updated.Name.Length == 0
            ? $"已清掉 {updated.Signature} 的名称。"
            : $"已把 {updated.Signature} 的名称改成「{updated.Name}」。";
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

        var target = MessageAutoReply == NoAutoReply ? null : MessageAutoReply;
        if (target == entry.AutoReply)
        {
            return;
        }

        var updated = _library.SetAutoReply(entry, target);
        RefreshAfterMessageEdit(updated.Index);
        StatusMessage = target is null
            ? $"已取消 {updated.Signature} 的自动回复。"
            : $"已把 {updated.Signature} 的自动回复设成「{target}」。";
    }

    /// <summary>
    /// MessageEntry 是一次性快照，改完属性必须重建列表，左栏的名字和 SxFy 才会跟着变。
    /// 改名或改头可能让这条消息不再匹配搜索词——用户正盯着它编辑，
    /// 让它从列表里消失太莫名，所以清掉搜索词再找回来。
    /// </summary>
    private void RefreshAfterMessageEdit(int index)
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

    internal void RegisterStructureNode(StructureNodeViewModel node) => _structureNodes.Add(node);

    private StructureNodeViewModel? FindStructureNode(XElement element) =>
        _structureNodes.FirstOrDefault(node => ReferenceEquals(node.Element, element));

    private void RaiseTreeCommandCanExecute()
    {
        AddChildCommand.RaiseCanExecuteChanged();
        AddSiblingCommand.RaiseCanExecuteChanged();
        DeleteNodeCommand.RaiseCanExecuteChanged();
    }

    private void LoadEditor(ConnectionProfile profile)
    {
        _loadingEditor = true;
        try
        {
            Role = profile.Role;
            Mode = profile.Mode;
            IpAddress = profile.IpAddress;
            Port = profile.Port;
            DeviceId = profile.DeviceId;
            T3Seconds = profile.T3Seconds;
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
            IpAddress = IpAddress,
            Port = Port,
            DeviceId = DeviceId,
            T3Seconds = T3Seconds,
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
