using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;
using Secs4Net;

namespace QoderSecs.Core.Connection;

/// <summary>
/// 界面上那一条「连接」。传输方式由参数里的 Transport 决定：
/// HSMS 走 <see cref="HsmsLink"/>，SECS-I 走 <see cref="Secs1Link"/>；
/// 记录、配对、日志、自动回复和阶段都挂在同一个 <see cref="SecsSession"/> 上，
/// 换传输方式不会换掉界面订阅的那一份记录。
/// </summary>
public sealed class ConnectionLink : IAsyncDisposable
{
    /// <summary>和 HsmsLink 同一个词：界面靠它区分「发出去了」和「没发出去」。</summary>
    public const string SentPrefix = SecsSession.SentPrefix;

    private readonly object _gate = new();
    private readonly SecsSession _session = new();
    private ISecsTransportLink? _active;

    /// <summary>测试替身：SECS-I 不打开串口，直接用内存管道。</summary>
    internal Func<ConnectionProfile, Stream>? Secs1StreamFactory { get; init; }

    public TranscriptCorrelator Transcript => _session.Transcript;

    public Func<MessageLibrary?>? LibraryProvider
    {
        get => _session.LibraryProvider;
        set => _session.LibraryProvider = value;
    }

    public Func<ConnectionProfile, string>? SessionDirectoryFactory
    {
        get => _session.SessionDirectoryFactory;
        set => _session.SessionDirectoryFactory = value;
    }

    public TranscriptLog? Log => _session.Log;

    public LinkPhase Phase => _session.Phase;

    public ConnectionProfile? Profile { get; private set; }

    public event EventHandler<LinkPhase>? PhaseChanged
    {
        add => _session.PhaseChanged += value;
        remove => _session.PhaseChanged -= value;
    }

    public bool CanConnect
    {
        get
        {
            lock (_gate)
            {
                return _active is null;
            }
        }
    }

    public void Connect(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ISecsTransportLink link;
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException("当前已经有一条连接，请先断开。");
            }

            link = profile.Transport switch
            {
                TransportKind.Secs1 => new Secs1Link(_session) { StreamFactory = Secs1StreamFactory },
                _ => new HsmsLink(_session)
            };
            _active = link;
            Profile = profile;
        }

        try
        {
            link.Connect(profile);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, link))
                {
                    _active = null;
                    Profile = null;
                }
            }

            throw;
        }
    }

    public async ValueTask DisconnectAsync()
    {
        ISecsTransportLink? link;
        lock (_gate)
        {
            link = _active;
            _active = null;
            Profile = null;
        }

        if (link is null)
        {
            await _session.CloseAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await link.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    public Task<string> SendAsync(SecsMessage message, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        ISecsTransportLink? link;
        lock (_gate)
        {
            link = _active;
        }

        return link?.SendAsync(message, cancellation)
            ?? throw new InvalidOperationException("尚未连接，不能发送。");
    }

    public bool CancelPendingSend()
    {
        ISecsTransportLink? link;
        lock (_gate)
        {
            link = _active;
        }

        return link?.CancelPendingSend() ?? false;
    }
}

/// <summary>ConnectionLink 只关心这三件事；两种传输各自实现。</summary>
internal interface ISecsTransportLink : IAsyncDisposable
{
    ConnectionProfile? Profile { get; }

    bool CanConnect { get; }

    void Connect(ConnectionProfile profile);

    ValueTask DisconnectAsync();

    Task<string> SendAsync(SecsMessage message, CancellationToken cancellation = default);

    bool CancelPendingSend();
}
