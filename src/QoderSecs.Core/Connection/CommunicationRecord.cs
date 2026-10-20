using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QoderSecs.Core.Connection;

/// <summary>通信记录里的一条。状态和耗时会随配对结果变化，因此实现 INotifyPropertyChanged。</summary>
public sealed class CommunicationRecord : INotifyPropertyChanged
{
    private RecordStatus _status;
    private int? _elapsedMilliseconds;
    private int? _linkedSystemBytes;

    public CommunicationRecord(
        SecsDirection direction,
        int systemBytes,
        SecsHeader header,
        DateTimeOffset timestamp,
        string? sml,
        RecordStatus status)
    {
        Direction = direction;
        SystemBytes = systemBytes;
        Header = header;
        Timestamp = timestamp;
        Sml = sml;
        _status = status;
    }

    public SecsDirection Direction { get; }

    public int SystemBytes { get; }

    public SecsHeader Header { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>小消息的完整 SML。大消息为 null，正文只在 sml.log 里，按 SmlSequence 去读。</summary>
    public string? Sml { get; }

    /// <summary>sml.log 里这条正文的序号；没开日志时为 null。</summary>
    public long? SmlSequence { get; internal set; }

    private long? _smlByteLength;

    /// <summary>正文的字节数：小消息是 SML 文本的长度，大消息是 sml.log 里那块正文的实际长度。</summary>
    public long? SmlByteLength
    {
        get => _smlByteLength;
        internal set
        {
            if (_smlByteLength == value)
            {
                return;
            }

            _smlByteLength = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SmlSizeText));
        }
    }

    public string SmlSizeText => SmlByteLength is not { } bytes
        ? string.Empty
        : bytes < 10240
            ? $"{bytes} B"
            : $"{bytes / 1024.0 / 1024.0:F2} MB";

    public RecordStatus Status
    {
        get => _status;
        internal set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>请求与配对回复之间的耗时；只有请求收到回复后才有值。</summary>
    public int? ElapsedMilliseconds
    {
        get => _elapsedMilliseconds;
        internal set
        {
            if (_elapsedMilliseconds == value)
            {
                return;
            }

            _elapsedMilliseconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ElapsedText));
        }
    }

    /// <summary>回复指向的请求，或请求收到的回复，用的都是 SystemBytes。</summary>
    public int? LinkedSystemBytes
    {
        get => _linkedSystemBytes;
        internal set
        {
            if (_linkedSystemBytes == value)
            {
                return;
            }

            _linkedSystemBytes = value;
            OnPropertyChanged();
        }
    }

    public string StatusText => _status switch
    {
        RecordStatus.Sent => Direction == SecsDirection.Out ? "已发送" : "已收到",
        RecordStatus.AwaitingSecondary => "等待回复",
        RecordStatus.SecondaryReceived => "收到回复",
        RecordStatus.TimedOut => "T3 超时",
        RecordStatus.Received => "已收到",
        RecordStatus.AwaitingReply => "待本端回复",
        RecordStatus.Answered => "已自动回复",
        RecordStatus.AutoReplySkipped => "未自动回复",
        RecordStatus.PairedSecondary => "配对回复",
        RecordStatus.LateSecondary => "迟到回复",
        RecordStatus.OrphanSecondary => "无人等待的回复",
        RecordStatus.LinkClosed => "连接已断开",
        RecordStatus.WaitAbandoned => "已取消等待",
        _ => _status.ToString()
    };

    public string ElapsedText => ElapsedMilliseconds is { } ms ? $"{ms} ms" : string.Empty;

    public string Signature => Header.Signature;

    /// <summary>列表里「消息」那一列：没有名称时只显示 SxFy。</summary>
    public string MessageText => string.IsNullOrWhiteSpace(Header.Name)
        ? Signature
        : $"{Signature} {Header.Name}";

    public string DirectionText => Direction == SecsDirection.Out ? "发出" : "收到";

    public string SystemBytesText => SystemBytes.ToString("X8");

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    /// <summary>
    /// 详情面板的整块文本。规格只要求时间和 SML 正文：方向、消息、SystemBytes、状态
    /// 列表那一行都有，不在这里重复。大消息正文在日志里，这里只给折叠行。
    /// </summary>
    public string DetailText
    {
        get
        {
            if (Sml is { } sml)
            {
                return $"时间     {TimeText}{Environment.NewLine}{Environment.NewLine}{sml}";
            }

            var size = SmlSizeText.Length == 0 ? "写入中" : SmlSizeText;
            return $"时间     {TimeText}{Environment.NewLine}{Environment.NewLine}" +
                   $"SML 共 {size}，正文在 sml.log 里，双击这里展开。";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
