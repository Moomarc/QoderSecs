namespace QoderSecs.Core.Connection;

/// <summary>
/// SECS-I 线上单独发送的控制字节。它们都小于块长度字节的最小值 10，
/// 但 NAK（0x15）除外——所以不能靠字节值猜类型，只能由收发双方当前处在
/// 「等一个控制字节」还是「等一个块」这一步来决定读几个字节。
/// </summary>
public enum Secs1Control
{
    /// <summary>准备接收。</summary>
    Eot = 0x04,

    /// <summary>请求发送。</summary>
    Enq = 0x05,

    /// <summary>收到，正确。</summary>
    Ack = 0x06,

    /// <summary>服务请求。本版只记录，不当成消息。</summary>
    Srq = 0x08,

    /// <summary>收到，有误，请重发。</summary>
    Nak = 0x15,

    /// <summary>设备标识确认。本版只记录。</summary>
    Eic = 0x7F
}

/// <summary>
/// SECS-I（SEMI E4）的一个数据块：长度字节 + 10 字节头 + 正文 + 两字节校验和。
/// 这里只碰字节，不碰连接和界面，所以每一处都能单独测。
/// </summary>
public sealed class Secs1Block
{
    /// <summary>头的字节数：设备号 2 + 消息号 2 + 块号 2 + 系统字节 4。</summary>
    public const int HeaderBytes = 10;

    /// <summary>校验和占两个字节，是大端序的「头 + 正文」逐字节相加。</summary>
    public const int ChecksumBytes = 2;

    /// <summary>长度字节最小值：只有头、没有正文。</summary>
    public const int MinLength = HeaderBytes;

    /// <summary>长度字节最大值，也是「头 + 正文」的总长上限。</summary>
    public const int MaxLength = 254;

    /// <summary>一块能装的正文字节数。大消息就按这个数切成好几块。</summary>
    public const int MaxBodyBytes = MaxLength - HeaderBytes;

    /// <summary>设备号 15 位，最高位是方向位 R。</summary>
    public const int MaxDeviceId = 0x7FFF;

    /// <summary>块号 15 位，最高位是结束位 E。</summary>
    public const int MaxBlockNumber = 0x7FFF;

    /// <summary>SECS-I 的头里流号只占 7 位，最高位让给 W 位；HSMS 的 255 到这里要收窄。</summary>
    public const int MaxStream = 0x7F;

    /// <summary>整块上线的字节数 = 长度字节 + 头 + 正文 + 校验和。</summary>
    public static int FrameBytes(int bodyBytes) => 1 + HeaderBytes + bodyBytes + ChecksumBytes;

    public Secs1Block(
        int deviceId,
        bool fromEquipment,
        SecsHeader header,
        int blockNumber,
        bool endOfMessage,
        int systemBytes,
        byte[] body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);

        if (deviceId is < 0 or > MaxDeviceId)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceId), $"设备号必须在 0 到 {MaxDeviceId} 之间。");
        }

        if (header.Stream is < 1 or > MaxStream)
        {
            throw new ArgumentOutOfRangeException(
                nameof(header),
                $"SECS-I 的头里流号只放得下 1 到 {MaxStream}，S{header.Stream} 发不出去（HSMS 可以到 255）。");
        }

        if (header.Function is < 1 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(header), $"功能号必须在 1 到 255 之间。");
        }

        if (blockNumber is < 0 or > MaxBlockNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(blockNumber), $"块号必须在 0 到 {MaxBlockNumber} 之间。");
        }

        if (body.Length > MaxBodyBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(body), $"一块最多装 {MaxBodyBytes} 字节正文。");
        }

        DeviceId = deviceId;
        FromEquipment = fromEquipment;
        Header = header;
        BlockNumber = blockNumber;
        EndOfMessage = endOfMessage;
        SystemBytes = systemBytes;
        Body = body;
    }

    public int DeviceId { get; }

    /// <summary>方向位 R：0 是 Host 发给设备，1 是设备发出来的。</summary>
    public bool FromEquipment { get; }

    /// <summary>流号、功能号和 W 位。名称只在界面里用，头里没有这个字段。</summary>
    public SecsHeader Header { get; }

    /// <summary>块号，从 1 开始递增。单块消息规范允许 0，实际设备基本都用 1。</summary>
    public int BlockNumber { get; }

    /// <summary>结束位 E：这一块是整条消息的最后一块。</summary>
    public bool EndOfMessage { get; }

    /// <summary>系统字节。同一条消息的每一块都相同，回复必须照抄请求的。</summary>
    public int SystemBytes { get; }

    /// <summary>这一块带的 SECS-II 正文（不是整条消息的正文）。</summary>
    public byte[] Body { get; }

    /// <summary>头的字节数加正文字节数，也就是长度字节的值。</summary>
    public int Length => HeaderBytes + Body.Length;

    /// <summary>块号 0 或 1 都算一条消息的第一块。</summary>
    public bool IsFirstBlock => BlockNumber is 0 or 1;

    /// <summary>拼成上线的整块字节：长度字节打头，校验和收尾。</summary>
    public byte[] Encode()
    {
        var frame = new byte[FrameBytes(Body.Length)];
        WriteTo(frame);
        return frame;
    }

    /// <summary>把整块写进调用方给的缓冲区，返回写掉的字节数。发送路径用它少攒一次数组。</summary>
    public int WriteTo(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var needed = FrameBytes(Body.Length);
        if (buffer.Length < needed)
        {
            throw new ArgumentException($"缓冲区至少要有 {needed} 字节。", nameof(buffer));
        }

        buffer[0] = (byte)Length;
        buffer[1] = (byte)(((DeviceId >> 8) & 0x7F) | (FromEquipment ? 0x80 : 0));
        buffer[2] = (byte)(DeviceId & 0xFF);
        buffer[3] = (byte)((Header.Stream & MaxStream) | (Header.ReplyExpected ? 0x80 : 0));
        buffer[4] = (byte)(Header.Function & 0xFF);
        buffer[5] = (byte)(((BlockNumber >> 8) & 0x7F) | (EndOfMessage ? 0x80 : 0));
        buffer[6] = (byte)(BlockNumber & 0xFF);
        buffer[7] = (byte)(SystemBytes >> 24);
        buffer[8] = (byte)(SystemBytes >> 16);
        buffer[9] = (byte)(SystemBytes >> 8);
        buffer[10] = (byte)SystemBytes;

        Body.CopyTo(buffer, HeaderBytes + 1);

        var sum = 0;
        for (var i = 1; i <= Length; i++)
        {
            sum += buffer[i];
        }

        buffer[Length + 1] = (byte)(sum >> 8);
        buffer[Length + 2] = (byte)sum;
        return needed;
    }

    /// <summary>
    /// 解一个整块。frame 是从长度字节开始、含两个校验字节的完整一段，
    /// 长度由调用方按长度字节读够了再交进来；这里再核一遍长度和校验和。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out Secs1Block? block, out string? error)
    {
        block = null;
        error = null;

        if (frame.Length < 1 + MinLength + ChecksumBytes)
        {
            error = $"整块太短，只有 {frame.Length} 字节。";
            return false;
        }

        var length = frame[0];
        if (length < MinLength || length > MaxLength)
        {
            error = $"长度字节 0x{length:X2} 不在 {MinLength} 到 {MaxLength} 之间。";
            return false;
        }

        if (frame.Length != length + 1 + ChecksumBytes)
        {
            error = $"按长度字节该有 {length + 1 + ChecksumBytes} 字节，实际只有 {frame.Length} 字节。";
            return false;
        }

        // 254 字节全 0xFF 也才 64770，不会越过 16 位；取低 16 位按大端拆两字节。
        var sum = 0;
        for (var i = 1; i <= length; i++)
        {
            sum += frame[i];
        }

        var high = (byte)(sum >> 8);
        var low = (byte)sum;
        if (high != frame[length + 1] || low != frame[length + 2])
        {
            error = $"校验和对不上：算出 {high:X2}{low:X2}，收到 {frame[length + 1]:X2}{frame[length + 2]:X2}。";
            return false;
        }

        var deviceId = ((frame[1] & 0x7F) << 8) | frame[2];
        var stream = frame[3] & 0x7F;
        var function = frame[4];
        var header = new SecsHeader(
            stream,
            function,
            (frame[3] & 0x80) != 0);
        var blockNumber = (((frame[5] & 0x7F) << 8) | frame[6]);
        var systemBytes = (frame[7] << 24) | (frame[8] << 16) | (frame[9] << 8) | frame[10];
        var body = frame[(1 + HeaderBytes)..(1 + length)].ToArray();

        // 流号和功能号是头里的两个字段，不是我们能挑的：真实机台完全可能发一个 S0 或 F0 过来。
        // 这种块当不成消息，但也不能让它把接收线程打死——所以在这里判掉，交出去就是一句 NAK 的理由。
        if (stream is 0)
        {
            error = "头里的流号是 S0，SECS-I 的流号只能是 1 到 127。";
            return false;
        }

        if (function is 0)
        {
            error = "头里的功能号是 F0，功能号只能是 1 到 255。";
            return false;
        }

        try
        {
            block = new Secs1Block(
                deviceId,
                (frame[1] & 0x80) != 0,
                header,
                blockNumber,
                (frame[5] & 0x80) != 0,
                systemBytes,
                body);
        }
        catch (ArgumentException exception)
        {
            // 兜住上面两条以外的所有头字段问题：一个叫 Try 的方法不能往外抛异常，
            // 因为抛出的是一个字的字节流，而接收线程只有一次。
            error = $"这块的头不合法：{exception.Message}";
            return false;
        }

        return true;
    }

    /// <summary>给人看的一行：哪条 SxFy、第几块、系统字节。</summary>
    public override string ToString()
    {
        var signature = Header.Signature;
        var end = EndOfMessage ? "末块" : "续块";
        return $"{signature} 块{BlockNumber} {end} sys={SystemBytes:X8} 正文{Body.Length}字节";
    }
}
