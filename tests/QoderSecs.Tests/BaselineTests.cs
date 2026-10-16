using Secs4Net;
using Secs4Net.Sml;

namespace QoderSecs.Tests;

[TestClass]
public sealed class BaselineTests
{
    [TestMethod]
    public void Secs4NetSmlRoundTripsMessageHeader()
    {
        using var source = new SecsMessage(1, 1, true) { Name = "Baseline" };
        var sml = SmlWriter.ToSml(source);
        using var message = SmlReader.ToSecsMessage(sml);

        Assert.AreEqual(source.S, message.S);
        Assert.AreEqual(source.F, message.F);
        Assert.AreEqual(source.ReplyExpected, message.ReplyExpected);
    }
}
