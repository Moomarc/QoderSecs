namespace QoderSecs.Core.Connection;

/// <summary>
/// 一条完整的 SECS-I 消息：一个方向、一次事务的通信单元。
/// 正文是编码好的 SECS-II 字节，传输层不管里面是什么。
/// </summary>
public sealed record Secs1Message(SecsHeader Header, int SystemBytes, byte[] Body)
{
    /// <summary>
    /// 按每块最多 244 字节切块，块号从 1 开始递增，最后一块置结束位。
    /// 空正文也出一块，因为线上一条消息至少要有长度字节、头和校验和。
    /// </summary>
    public IReadOnlyList<Secs1Block> ToBlocks(int deviceId, bool fromEquipment)
    {
        var body = Body ?? [];
        var count = Math.Max(1, (body.Length + Secs1Block.MaxBodyBytes - 1) / Secs1Block.MaxBodyBytes);
        if (count > Secs1Block.MaxBlockNumber)
        {
            // 块号只有 15 位：正文超过约 7.5MB 就切不下一条消息。这条要说成人话，
            // 不能让调用方收到一个 ArgumentOutOfRangeException——它只会当成程序坏了。
            throw new ArgumentOutOfRangeException(
                nameof(Body),
                $"这条消息正文 {body.Length} 字节要切成 {count} 块，超过 SECS-I 块号的上限 " +
                $"{Secs1Block.MaxBlockNumber}（一条消息装不下，请用 HSMS）。");
        }

        var blocks = new List<Secs1Block>(count);

        for (var index = 0; index < count; index++)
        {
            var start = index * Secs1Block.MaxBodyBytes;
            var length = Math.Min(Secs1Block.MaxBodyBytes, body.Length - start);
            var chunk = length > 0 ? body[start..(start + length)] : [];
            blocks.Add(new Secs1Block(
                deviceId,
                fromEquipment,
                Header,
                index + 1,
                index == count - 1,
                SystemBytes,
                chunk));
        }

        return blocks;
    }

    public override string ToString() => $"{Header.Signature} sys={SystemBytes:X8} 正文{(Body ?? []).Length}字节";
}

/// <summary>
/// 把收到的块攒回一条消息。规矩只有两条，都来自线上格式本身：
/// 同一条消息的每一块系统字节相同，块号连续递增，结束位落在最后一块上。
/// 中间断了就整条丢掉并说清丢掉的原因——宁可少一条消息，也不拼出一条缺了块的坏消息。
/// </summary>
public sealed class Secs1Assembler
{
    /// <summary>攒到的块，或一条拼完的消息，外加一句这次为什么不对。</summary>
    public sealed record Outcome(Secs1Message? Message, string? Problem);

    private readonly List<Secs1Block> _blocks = [];

    /// <summary>已经攒着、还没等到结束位的块数。</summary>
    public int PendingBlocks => _blocks.Count;

    /// <summary>正在攒的这条消息的系统字节；没在攒的时候为 null。</summary>
    public int? PendingSystemBytes => _blocks.Count > 0 ? _blocks[0].SystemBytes : null;

    public static readonly Outcome Ignored = new(null, null);

    public Outcome Feed(Secs1Block block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (_blocks.Count == 0)
        {
            return Start(block);
        }

        var first = _blocks[0];
        var last = _blocks[^1];

        if (block.SystemBytes != first.SystemBytes)
        {
            var problem = $"系统字节从 {first.SystemBytes:X8} 变成 {block.SystemBytes:X8}，" +
                          $"丢掉已攒的 {Describe(first, last)}，从头开始收。";
            _blocks.Clear();
            Start(block);
            return new Outcome(null, problem);
        }

        if (block.BlockNumber != last.BlockNumber + 1)
        {
            var problem = $"块号不连续：期望 {last.BlockNumber + 1}，收到 {block.BlockNumber}，" +
                          $"丢掉已攒的 {Describe(first, last)}。";
            _blocks.Clear();
            if (!block.IsFirstBlock)
            {
                return new Outcome(null, problem);
            }

            Start(block);
            return new Outcome(null, problem);
        }

        return Accept(block);
    }

    /// <summary>超时或断开时把半条消息扔掉，别让它污染下一条。</summary>
    public string? Abandon(string reason)
    {
        if (_blocks.Count == 0)
        {
            return null;
        }

        var problem = $"丢掉没凑齐的消息：{Describe(_blocks[0], _blocks[^1])}，{reason}。";
        _blocks.Clear();
        return problem;
    }

    private Outcome Start(Secs1Block block)
    {
        if (!block.IsFirstBlock)
        {
            return new Outcome(null, $"第一块的块号是 {block.BlockNumber}，不是 1，这一块没法认。");
        }

        return Accept(block);
    }

    private Outcome Accept(Secs1Block block)
    {
        _blocks.Add(block);
        if (!block.EndOfMessage)
        {
            return Ignored;
        }

        var message = Build();
        _blocks.Clear();
        return new Outcome(message, null);
    }

    private Secs1Message Build()
    {
        var first = _blocks[0];
        var total = 0;
        foreach (var block in _blocks)
        {
            total += block.Body.Length;
        }

        var body = new byte[total];
        var offset = 0;
        foreach (var block in _blocks)
        {
            block.Body.CopyTo(body, offset);
            offset += block.Body.Length;
        }

        return new Secs1Message(first.Header, first.SystemBytes, body);
    }

    private static string Describe(Secs1Block first, Secs1Block last) =>
        $"{first.Header.Signature} sys={first.SystemBytes:X8} 块{first.BlockNumber}..{last.BlockNumber}";
}
