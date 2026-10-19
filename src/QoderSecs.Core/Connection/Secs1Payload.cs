using System.Buffers;
using Secs4Net;

namespace QoderSecs.Core.Connection;

/// <summary>
/// SECS-I 的正文就是编码好的 SECS-II 项。这里只在 SecsMessage 和上线的帧之间换算，
/// 编码和解码都用 Secs4Net 自己的那一份——同一项内容在 HSMS 和 SECS-I 上不该有两种写法。
/// </summary>
public static class Secs1Payload
{
    /// <summary>把要发的消息变成上线的帧。系统字节由调用方给，SECS-I 头里没有它。</summary>
    public static Secs1Message ToFrame(SecsMessage message, int systemBytes)
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = message.SecsItem is { } item ? Encode(item) : [];
        return new Secs1Message(
            new SecsHeader(message.S, message.F, message.ReplyExpected, message.Name),
            systemBytes,
            body);
    }

    /// <summary>
    /// 收进来的帧变成消息。正文解不开就返回 false 并给一句能直接进状态栏的话。
    /// 解出来的 Item 归调用方释放，和 HSMS 那边一条消息的归属一样。
    /// </summary>
    public static bool TryToMessage(Secs1Message frame, out SecsMessage? message, out string? error)
    {
        ArgumentNullException.ThrowIfNull(frame);

        message = null;
        Item? item = null;
        if (frame.Body.Length > 0 && !TryDecodeItem(frame.Body, out item, out error))
        {
            return false;
        }

        var built = new SecsMessage((byte)frame.Header.Stream, (byte)frame.Header.Function, frame.Header.ReplyExpected);
        if (item is not null)
        {
            built.SecsItem = item;
        }

        message = built;
        error = null;
        return true;
    }

    /// <summary>把一个项编成 SECS-II 字节：长度用几字节宽由 Secs4Net 定，跟 HSMS 那条路用的是同一份。</summary>
    public static byte[] Encode(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var writer = new ArrayBufferWriter<byte>();
        item.EncodeTo(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static bool TryDecodeItem(byte[] body, out Item? item, out string? error)
    {
        item = null;
        error = null;
        var sequence = new ReadOnlySequence<byte>(body);
        try
        {
            item = Item.DecodeFromFullBuffer(ref sequence);
        }
        catch (Exception exception)
        {
            // 上线的字节是对方给的，什么都能凑出来：整包换掉异常，只留一句人话。
            item = null;
            error = $"正文不是合法的 SECS-II 项：{exception.Message}";
            return false;
        }

        if (sequence.Length > 0)
        {
            item.Dispose();
            item = null;
            error = $"正文解完一项还多出 {sequence.Length} 字节，末尾不是同一条 SECS-II。";
            return false;
        }

        return true;
    }
}
