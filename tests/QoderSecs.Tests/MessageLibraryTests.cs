using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class MessageLibraryTests
{
    private static string ReadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml"));

    [TestMethod]
    public void Load_ParsesMessagesInDocumentOrder()
    {
        var library = MessageLibrary.Load(ReadFixture());

        Assert.HasCount(3, library.Messages);

        var first = library.Messages[0];
        Assert.AreEqual("S1F1_AreYouThere", first.Name);
        Assert.AreEqual(1, first.Stream);
        Assert.AreEqual(1, first.Function);
        Assert.IsTrue(first.WBit);
        Assert.AreEqual("S1F2_OnlineData", first.AutoReply);
        Assert.IsNotNull(first.Structure);
        Assert.IsFalse(first.Structure!.HasElements);
        Assert.AreEqual("S1F1", first.Signature);

        var second = library.Messages[1];
        Assert.IsFalse(second.WBit);
        Assert.IsNull(second.AutoReply);
        Assert.IsNotNull(second.Structure);

        var third = library.Messages[2];
        Assert.AreEqual(7, third.Stream);
        Assert.AreEqual(25, third.Function);
    }

    [TestMethod]
    public void Export_ReturnsOriginalTextUnchanged()
    {
        var original = ReadFixture();
        var library = MessageLibrary.Load(original);

        // 根元素外换行和空元素写法�?X/> �?<X />）不属于 spec 承诺的保真范�?        Assert.AreEqual(Normalize(original), Normalize(library.Export()));
    }

    private static string Normalize(string xml) =>
        xml.Replace(" />", "/>").TrimEnd('\r', '\n');

    [TestMethod]
    public void RoundTrip_PreservesUnknownAttributesCommentsAndMaps()
    {
        var reloaded = MessageLibrary.Load(MessageLibrary.Load(ReadFixture()).Export());

        var body = reloaded.Messages[2].Structure!
            .Descendants("A")
            .Single(element => element.Attribute("Name")?.Value == "PPBODY");
        Assert.AreEqual("keep", body.Attribute("ExtraAttr")?.Value);
        Assert.AreEqual("[Section1]\nKey1=1\nKey2=two\n", body.Element("Value")!.Value);

        var maps = reloaded.Document.Descendants("Maps").ToList();
        Assert.HasCount(7, maps);
        Assert.IsTrue(maps.All(map => map.Attribute("Type") is not null));

        Assert.IsTrue(reloaded.Document.DescendantNodes().OfType<XComment>().Any());
        Assert.IsNotNull(reloaded.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry"));
    }

    [TestMethod]
    public void Load_RejectsMessageWithoutFunction()
    {
        const string xml = """
            <SXML>
                <MessageDictionary>
                    <Message Name="Broken">
                        <Stream>1</Stream>
                    </Message>
                </MessageDictionary>
            </SXML>
            """;

        Assert.ThrowsExactly<FormatException>(() => MessageLibrary.Load(xml));
    }

    [TestMethod]
    public void Load_RejectsDocumentWithoutMessageDictionary()
    {
        Assert.ThrowsExactly<FormatException>(() => MessageLibrary.Load("<SXML></SXML>"));
    }

    [TestMethod]
    public void Clear_RemovesAllMessages()
    {
        var library = MessageLibrary.Load(ReadFixture());

        library.Clear();

        Assert.IsEmpty(library.Messages);
        Assert.IsNotNull(library.Document.Root!.Element("MessageDictionary"));
    }

    // ---- 2.4e 消息属性 ----

    private static string[] ChildOrder(XElement message) =>
        [.. message.Elements().Select(element => element.Name.LocalName)];

    [TestMethod]
    public void SetName_RewritesTheAttributeAndKeepsTheRestOfTheMessage()
    {
        var library = MessageLibrary.Load(ReadFixture());

        var updated = library.SetName(library.Messages[0], "  AreYouThere_Renamed  ");

        Assert.AreEqual("AreYouThere_Renamed", updated.Name);
        Assert.AreEqual("link test", updated.Element.Attribute("Description")?.Value);
        Assert.AreEqual("S1F2_OnlineData", updated.AutoReply);
        Assert.AreEqual("S1F1", updated.Signature);
        Assert.AreEqual(0, updated.Index);
        Assert.HasCount(3, library.Messages);
        Assert.IsTrue(library.Export().Contains("Name=\"AreYouThere_Renamed\"", StringComparison.Ordinal));
        Assert.IsFalse(library.Export().Contains("S1F1_AreYouThere", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SetName_BlankRemovesTheAttribute()
    {
        var library = MessageLibrary.Load(ReadFixture());

        var updated = library.SetName(library.Messages[1], "   ");

        Assert.AreEqual(string.Empty, updated.Name);
        Assert.IsNull(updated.Element.Attribute("Name"));
        Assert.AreEqual("Description", updated.Element.Attributes().Single().Name.LocalName);
    }

    [TestMethod]
    public void SetHeader_RewritesStreamFunctionAndPutsWBitAfterFunction()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var second = library.Messages[1];
        Assert.IsFalse(second.WBit);

        var updated = library.SetHeader(second, 10, 3, true);

        Assert.AreEqual("S10F3", updated.Signature);
        Assert.IsTrue(updated.WBit);
        CollectionAssert.AreEqual(
            new[] { "Stream", "Function", "WBit", "Structure" },
            ChildOrder(updated.Element));
        Assert.AreEqual("1", updated.Element.Element("WBit")!.Value);
    }

    [TestMethod]
    public void SetHeader_FalseRemovesTheWBitElementAndKeepsAutoReply()
    {
        var library = MessageLibrary.Load(ReadFixture());

        var updated = library.SetHeader(library.Messages[0], 1, 1, false);

        Assert.IsFalse(updated.WBit);
        Assert.IsNull(updated.Element.Element("WBit"));
        Assert.AreEqual("S1F2_OnlineData", updated.AutoReply);
        CollectionAssert.AreEqual(
            new[] { "Stream", "Function", "AutoReply", "Structure" },
            ChildOrder(updated.Element));
    }

    [TestMethod]
    public void SetHeader_RejectsOutOfRangeValues()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[0];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.SetHeader(entry, 0, 1, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.SetHeader(entry, 1, 256, true));

        // 校验在写之前，两个值都不合法时消息头一个字节都不能动。
        Assert.AreEqual("S1F1", library.Messages[0].Signature);
        Assert.IsTrue(library.Messages[0].WBit);
        Assert.AreEqual("S1F1_AreYouThere", library.Messages[0].Name);
    }

    [TestMethod]
    public void SetAutoReply_InsertsBetweenWBitAndStructure()
    {
        var library = MessageLibrary.Load(ReadFixture());

        var updated = library.SetAutoReply(library.Messages[2], "S1F2_OnlineData");

        Assert.AreEqual("S1F2_OnlineData", updated.AutoReply);
        CollectionAssert.AreEqual(
            new[] { "Stream", "Function", "WBit", "AutoReply", "Structure" },
            ChildOrder(updated.Element));
    }

    [TestMethod]
    public void SetAutoReply_OnMessageWithoutWBitGoesAfterFunction()
    {
        var library = MessageLibrary.Load(ReadFixture());

        var updated = library.SetAutoReply(library.Messages[1], "S1F1_AreYouThere");

        CollectionAssert.AreEqual(
            new[] { "Stream", "Function", "AutoReply", "Structure" },
            ChildOrder(updated.Element));
        Assert.AreEqual("S1F1_AreYouThere", library.Messages[1].AutoReply);
    }

    [TestMethod]
    public void SetAutoReply_NullRemovesTheElement()
    {
        var library = MessageLibrary.Load(ReadFixture());

        var updated = library.SetAutoReply(library.Messages[0], null);

        Assert.IsNull(updated.AutoReply);
        Assert.IsNull(updated.Element.Element("AutoReply"));
        Assert.IsTrue(updated.WBit);
        Assert.IsFalse(library.Export().Contains("AutoReply", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MessageEdits_LeaveTheRestOfTheDocumentAlone()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[0];

        library.SetName(entry, "Renamed");
        library.SetHeader(library.Messages[0], 5, 1, false);
        library.SetAutoReply(library.Messages[0], null);

        var edited = library.Messages[0];
        Assert.AreEqual("Renamed", edited.Name);
        Assert.AreEqual("S5F1", edited.Signature);
        Assert.IsFalse(edited.WBit);
        Assert.IsNull(edited.AutoReply);

        // 其余部分必须是原样：DataDictionary、注释、Maps、没动过的消息。
        Assert.AreEqual("keep me", library.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry")!.Value);
        Assert.IsTrue(library.Document.DescendantNodes().OfType<XComment>().Any());
        Assert.HasCount(7, library.Document.Descendants("Maps").ToList());

        var untouched = library.Messages[2];
        Assert.AreEqual("RecipeBody", untouched.Name);
        Assert.AreEqual("keep", untouched.Structure!
            .Descendants("A")
            .Single(element => element.Attribute("Name")?.Value == "PPBODY")
            .Attribute("ExtraAttr")!.Value);
        Assert.HasCount(3, library.Messages);
    }
}

