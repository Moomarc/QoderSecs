using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

/// <summary>
/// 通信记录筛选框认的写法。完整签名和消息库搜索共用 TryParseSignature；
/// 只写 Stream（S6 / 6）是筛选框独有的一档，F 单独写不认。
/// </summary>
[TestClass]
public sealed class SignatureFilterParsingTests
{
    [TestMethod]
    public void FullSignatureIsParsedAsStreamAndFunction()
    {
        foreach (var spelling in new[] { "S6F11", "s6f11", "6F11", "6 11", "6,11", "S6 F11" })
        {
            Assert.IsTrue(MessageLibrary.TryParseSignatureOrStream(spelling, out var stream, out var function), spelling);
            Assert.AreEqual(6, stream, spelling);
            Assert.AreEqual(11, function, spelling);
        }
    }

    [TestMethod]
    public void LoneStreamLetterOrNumberMeansTheWholeStream()
    {
        foreach (var spelling in new[] { "S6", "s6", "6", " S6 " })
        {
            Assert.IsTrue(MessageLibrary.TryParseSignatureOrStream(spelling, out var stream, out var function), spelling);
            Assert.AreEqual(6, stream, spelling);
            Assert.IsNull(function, spelling);
        }
    }

    [TestMethod]
    public void LoneFunctionIsNotAccepted()
    {
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("F11", out _, out _));
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("f11", out _, out _));
    }

    [TestMethod]
    public void RangesStayOneToTwoFiveFive()
    {
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("0", out _, out _));
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("S256", out _, out _));
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("S6F0", out _, out _));
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("S6F256", out _, out _));
        Assert.IsTrue(MessageLibrary.TryParseSignatureOrStream("255", out var last, out var none));
        Assert.AreEqual(255, last);
        Assert.IsNull(none);
    }

    [TestMethod]
    public void NonsenseStaysRejected()
    {
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("S6F", out _, out _));
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("S", out _, out _));
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("SF11", out _, out _));
        Assert.IsFalse(MessageLibrary.TryParseSignatureOrStream("6F11F", out _, out _));
    }
}
