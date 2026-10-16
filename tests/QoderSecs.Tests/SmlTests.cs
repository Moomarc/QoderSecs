using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class SmlTests
{
    private static MessageLibrary Sample() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));

    private static MessageEntry Entry(string structure) => MessageLibrary.Load($"""
        <SXML>
            <MessageDictionary>
                <Message Name="M"><Stream>1</Stream><Function>1</Function>{structure}</Message>
            </MessageDictionary>
        </SXML>
        """).Messages[0];

    private static string Nl(string text) => text.Replace("\r\n", "\n");

    private static ParsedSml Parse(string sml)
    {
        Assert.IsTrue(SmlParser.TryParse(sml, out var parsed, out var error), error);
        return parsed!;
    }

    private static string? ParseError(string sml)
    {
        Assert.IsFalse(SmlParser.TryParse(sml, out _, out var error), error);
        return error;
    }

    // ---------- 预览 ----------

    [TestMethod]
    public void TryToSml_EmptyStructureWritesHeaderOnly()
    {
        Assert.IsTrue(SmlConverter.TryToSml(Sample().Messages[0], out var sml, out var error));

        Assert.IsNull(error);
        Assert.AreEqual("'S1F1'W\n.\n", Nl(sml));
    }

    [TestMethod]
    public void TryToSml_WritesListWithItsValues()
    {
        Assert.IsTrue(SmlConverter.TryToSml(Sample().Messages[1], out var sml, out _));

        var expected = string.Join("\n",
            "'S1F2'",
            "    <L [2] ",
            "        <A [9] 'QODERSECS'>",
            "        <A [3] '1.0'>",
            "    >",
            ".",
            string.Empty);
        Assert.AreEqual(expected, Nl(sml));
    }

    [TestMethod]
    public void TryToSml_KeepsEveryTypeOfTheRecipeBody()
    {
        Assert.IsTrue(SmlConverter.TryToSml(Sample().Messages[2], out var raw, out _));
        var sml = Nl(raw);

        StringAssert.StartsWith(sml, "'S7F25'W");
        StringAssert.Contains(sml, "'TEST-RECIPE'");
        StringAssert.Contains(sml, "'[Section1]\nKey1=1\nKey2=two\n'");
        StringAssert.Contains(sml, "<B [2] 0x00 0xFF>");
        StringAssert.Contains(sml, "<Boolean [1] True>");
        StringAssert.Contains(sml, "<U4 [1] 42>");
    }

    [TestMethod]
    public void TryToSml_ReportsInvalidValue()
    {
        var entry = Entry("<Structure><U4><Value>abc</Value></U4></Structure>");

        Assert.IsFalse(SmlConverter.TryToSml(entry, out var sml, out var error));
        Assert.AreEqual(string.Empty, sml);
        StringAssert.Contains(error!, "abc");
    }

    [TestMethod]
    public void TryToSml_ReportsNumericArray()
    {
        var entry = Entry("<Structure><U4><Value>1</Value><Value>2</Value></U4></Structure>");

        Assert.IsFalse(SmlConverter.TryToSml(entry, out _, out var error));
        StringAssert.Contains(error!, "数组");
    }

    [TestMethod]
    public void TryToSml_AcceptsSecsExpressSpaceSeparatedBinary()
    {
        var entry = Entry("<Structure><B><Value>20 20 C3 50 00 00 00 01</Value></B></Structure>");

        Assert.IsTrue(SmlConverter.TryToSml(entry, out var sml, out var error), error);
        StringAssert.Contains(Nl(sml), "<B [8] 0x20 0x20 0xC3 0x50 0x00 0x00 0x00 0x01>");
    }

    [TestMethod]
    public void TryToSml_SendsEmptyNumericPlaceholderAsZero()
    {
        var entry = Entry(
            "<Structure><L><U4><Value></Value></U4><U2><Value> </Value></U2><F4><Value></Value></F4></L></Structure>");

        Assert.IsTrue(SmlConverter.TryToSml(entry, out var sml, out var error), error);
        var text = Nl(sml);
        StringAssert.Contains(text, "<U4 [1] 0>");
        StringAssert.Contains(text, "<U2 [1] 0>");
        StringAssert.Contains(text, "<F4 [1] 0>");
    }

    [TestMethod]
    public void TryToSml_EmptyBinaryDoesNotThrow()
    {
        var entry = Entry("<Structure><B><Value></Value></B></Structure>");

        Assert.IsTrue(SmlConverter.TryToSml(entry, out var sml, out var error), error);
        StringAssert.StartsWith(Nl(sml), "'S1F1'");
    }

    /// <summary>大 PP body 不进预览框：正文换成一句说明，界面才不会被几十兆文本拖死。</summary>
    [TestMethod]
    public void TryToPreviewSml_SkipsABodyThatIsTooBigForThePreviewBox()
    {
        var entry = Entry($"<Structure><B><Value>{string.Concat(Enumerable.Repeat("AB ", 2048))}</Value></B></Structure>");
        const int cap = 4 * 1024;

        Assert.IsTrue(SmlConverter.TryToPreviewSml(entry, cap, out var preview, out var error), error);
        StringAssert.Contains(preview, "超过预览上限");
        Assert.IsLessThan(cap, preview.Length, "说明本身必须比上限短得多，否则等于没截断");
    }

    /// <summary>没超上限的正文照旧给全文，粘贴、比对都还要靠它。</summary>
    [TestMethod]
    public void TryToPreviewSml_KeepsTheWholeBodyWhenItFits()
    {
        var entry = Entry("<Structure><B><Value>20 20 C3 50</Value></B></Structure>");

        Assert.IsTrue(SmlConverter.TryToPreviewSml(entry, 4 * 1024, out var preview, out _));
        Assert.IsTrue(SmlConverter.TryToSml(entry, out var full, out _));
        Assert.AreEqual(full, preview);
    }

    /// <summary>发送可用性每敲一下都要判一次，判定不能靠生成正文。</summary>
    [TestMethod]
    public void TryValidate_ReportsTheSameProblemWithoutBuildingTheBody()
    {
        Assert.IsFalse(SmlConverter.TryValidate(Entry("<Structure><U4><Value>abc</Value></U4></Structure>"), out var error));
        StringAssert.Contains(error!, "abc");
        Assert.IsTrue(SmlConverter.TryValidate(Entry("<Structure><B><Value>01 02</Value></B></Structure>"), out _));
    }

    // ---------- 发送用消息 ----------

    [TestMethod]
    public void TryToMessage_CarriesHeaderAndWBit()
    {
        var entry = Sample().Messages[0];

        Assert.IsTrue(SmlConverter.TryToMessage(entry, out var message, out var error), error);
        using (message)
        {
            Assert.AreEqual(1, (int)message.S);
            Assert.AreEqual(1, (int)message.F);
            Assert.IsTrue(message.ReplyExpected);
        }
    }

    /// <summary>发送用的消息和预览用的 SML 必须同源，否则记录里的 SML 粘不回来。</summary>
    [TestMethod]
    public void TryToMessage_WritesTheSameSmlAsThePreview()
    {
        var entry = Sample().Messages[2];

        Assert.IsTrue(SmlConverter.TryToSml(entry, out var preview, out _));
        Assert.IsTrue(SmlConverter.TryToMessage(entry, out var message, out _));
        using (message)
        {
            Assert.AreEqual(preview, SmlConverter.ToSml(message));
        }
    }

    /// <summary>名称要能进通信记录，但不能出现在 SML 里，否则粘不回来。</summary>
    [TestMethod]
    public void TryToMessage_CarriesTheNameButKeepsItOutOfTheSml()
    {
        var entry = Sample().Messages[2];

        Assert.IsTrue(SmlConverter.TryToMessage(entry, out var message, out var error), error);
        using (message)
        {
            Assert.AreEqual("RecipeBody", message.Name);

            var sml = Nl(SmlConverter.ToSml(message));
            StringAssert.StartsWith(sml, "'S7F25'W");
            Assert.IsFalse(sml.Contains("RecipeBody", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void TryToMessage_KeepsTheStructureAsItem()
    {
        var entry = Entry("<Structure><L><A><Value>QODERSECS</Value></A></L></Structure>");

        Assert.IsTrue(SmlConverter.TryToMessage(entry, out var message, out var error), error);
        using (message)
        {
            Assert.IsFalse(message.ReplyExpected);
            Assert.IsNotNull(message.SecsItem);
        }
    }

    [TestMethod]
    public void TryToMessage_ReportsInvalidValueWithoutAMessage()
    {
        var entry = Entry("<Structure><U1><Value>300</Value></U1></Structure>");

        Assert.IsFalse(SmlConverter.TryToMessage(entry, out var message, out var error));

        Assert.IsNull(message);
        Assert.IsNotNull(error);
    }

    // ---------- 粘贴 ----------

    [TestMethod]
    public void TryParse_AcceptsTheDialectUsedByHandWrittenTemplates()
    {
        const string sml = """
            S1F1 W
            <L
              <A "MDLN-A">
              <A "REV1.0">
            >.
            """;

        var parsed = Parse(sml);
        Assert.AreEqual((byte)1, parsed.Stream);
        Assert.AreEqual((byte)1, parsed.Function);
        Assert.IsTrue(parsed.ReplyExpected);

        var list = parsed.Structure.Element("L")!;
        var items = list.Elements("A").ToList();
        Assert.HasCount(2, items);
        Assert.AreEqual("MDLN-A", items[0].Element("Value")!.Value);
        Assert.AreEqual("REV1.0", items[1].Element("Value")!.Value);
        Assert.AreEqual("4096", items[0].Element("Maps")!.Attribute("Type")!.Value);
        Assert.IsNull(list.Element("Maps"));
    }

    [TestMethod]
    public void TryParse_AcceptsLowercaseTagsAndHexWithoutCount()
    {
        const string sml = """
            s5f1
            <l
              <B 0x81>
              <U2 1001>
              <A "ON FIRE">
            >.
            """;

        var parsed = Parse(sml);
        Assert.AreEqual((byte)5, parsed.Stream);
        Assert.IsFalse(parsed.ReplyExpected);

        var list = parsed.Structure.Element("L")!;
        Assert.AreEqual("81", list.Element("B")!.Element("Value")!.Value);
        Assert.AreEqual("1024", list.Element("B")!.Element("Maps")!.Attribute("Type")!.Value);
        Assert.AreEqual("1001", list.Element("U2")!.Element("Value")!.Value);
        Assert.AreEqual("32", list.Element("U2")!.Element("Maps")!.Attribute("Type")!.Value);
        Assert.AreEqual("ON FIRE", list.Element("A")!.Element("Value")!.Value);
    }

    [TestMethod]
    public void TryParse_AcceptsHeaderWithoutItems()
    {
        var parsed = Parse("S1F99 W.");

        Assert.AreEqual((byte)1, parsed.Stream);
        Assert.AreEqual((byte)99, parsed.Function);
        Assert.IsTrue(parsed.ReplyExpected);
        Assert.IsFalse(parsed.Structure.HasElements);
    }

    [TestMethod]
    public void TryParse_AcceptsSingleDigitHexAndGluedCount()
    {
        var parsed = Parse("""
            S1F14
            <L
              <B 0x0>
              <L
                <A[4]"MDLN">
                <A "REV1.0">
              >
            >.
            """);

        var outer = parsed.Structure.Element("L")!;
        Assert.AreEqual("00", outer.Element("B")!.Element("Value")!.Value);

        var inner = outer.Element("L")!;
        Assert.AreEqual("MDLN", inner.Elements("A").First().Element("Value")!.Value);
        Assert.AreEqual("REV1.0", inner.Elements("A").Last().Element("Value")!.Value);
    }

    [TestMethod]
    public void TryParse_SplitsGluedCountBeforeLookingUpTheType()
    {
        StringAssert.Contains(ParseError("S2F18\n<NOW[16]>.\n")!, "NOW");
        StringAssert.Contains(ParseError("S1F1 W\n<A[x] 'MDLN'>\n.")!, "个数标注");
    }

    [TestMethod]
    public void TryParse_ReadsBackWhatTryToSmlWrote()
    {
        var entry = Sample().Messages[2];
        Assert.IsTrue(SmlConverter.TryToSml(entry, out var sml, out _));

        var parsed = Parse(sml);
        Assert.AreEqual(entry.Stream, (int)parsed.Stream);
        Assert.AreEqual(entry.Function, (int)parsed.Function);
        Assert.AreEqual(entry.WBit, parsed.ReplyExpected);

        var body = parsed.Structure.Descendants("A")
            .Single(element => element.Element("Value")!.Value.StartsWith("[Section1]", StringComparison.Ordinal));
        Assert.AreEqual("[Section1]\nKey1=1\nKey2=two\n", Nl(body.Element("Value")!.Value));

        var list = parsed.Structure.Element("L")!;
        Assert.AreEqual("00FF", list.Element("B")!.Element("Value")!.Value);
        Assert.AreEqual("True", list.Element("Boolean")!.Element("Value")!.Value);
        Assert.AreEqual("42", list.Element("U4")!.Element("Value")!.Value);
        Assert.AreEqual("64", list.Element("U4")!.Element("Maps")!.Attribute("Type")!.Value);
    }

    [TestMethod]
    public void TryParse_RejectsUnknownElementType()
    {
        StringAssert.Contains(ParseError("S2F33 W\n<L [1]\n  <U4AUTO [1] >\n>\n.")!, "U4AUTO");
    }

    [TestMethod]
    public void TryParse_RejectsNumericArray()
    {
        StringAssert.Contains(ParseError("S1F1 W\n<U4 [3] 1 2 3>\n.")!, "数组");
    }

    [TestMethod]
    public void TryParse_AcceptsElementWithoutValueAsPlaceholder()
    {
        var parsed = Parse("S1F1 W\n<U4 [1] >\n.");

        Assert.AreEqual(string.Empty, parsed.Structure.Element("U4")!.Element("Value")!.Value);
        Assert.AreEqual("0", new StructureNode(parsed.Structure.Element("U4")!).SingleValue);
    }

    [TestMethod]
    public void TryParse_RejectsUnterminatedString()
    {
        StringAssert.Contains(ParseError("S1F1 W\n<A \"MDLN>\n.")!, "引号");
    }

    [TestMethod]
    public void TryParse_RejectsMoreThanOneMessage()
    {
        StringAssert.Contains(ParseError("S1F1 W\n.\nS1F2\n.\n")!, "一条消息");
    }

    [TestMethod]
    public void TryParse_RejectsStreamOutOfRange()
    {
        StringAssert.Contains(ParseError("S300F1 W\n.")!, "255");
    }

    [TestMethod]
    public void TryParse_RejectsTextThatIsNotSml()
    {
        foreach (var sml in new[] { string.Empty, "   ", "hello", "<A \"x\">", "S1F1 W\n<L\n<A \"x\">\n" })
        {
            Assert.IsFalse(string.IsNullOrEmpty(ParseError(sml)), sml);
        }
    }

    // ---------- 粘贴应用到消息 ----------

    [TestMethod]
    public void ApplySml_ReplacesHeaderAndStructureButKeepsEverythingElse()
    {
        var library = Sample();
        var parsed = Parse("S2F41\n<L\n  <U4 7>\n>.");

        var updated = library.ApplySml(library.Messages[0], parsed);

        Assert.AreEqual(2, updated.Stream);
        Assert.AreEqual(41, updated.Function);
        Assert.IsFalse(updated.WBit);
        Assert.IsNull(updated.Element.Element("WBit"));
        Assert.AreEqual("S1F1_AreYouThere", updated.Name);
        Assert.AreEqual("S1F2_OnlineData", updated.AutoReply);
        Assert.AreEqual("7", updated.Structure!.Element("L")!.Element("U4")!.Element("Value")!.Value);
        Assert.AreEqual("64", updated.Structure!.Element("L")!.Element("U4")!.Element("Maps")!.Attribute("Type")!.Value);
        CollectionAssert.AreEqual(
            new[] { "S1F1_AreYouThere" },
            library.Query("2 41").Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void ApplySml_InsertsWBitInFrontOfStructure()
    {
        var library = Sample();

        var updated = library.ApplySml(library.Messages[1], Parse("S1F14 W\n<Boolean True>\n."));

        Assert.IsTrue(updated.WBit);
        CollectionAssert.AreEqual(
            new[] { "Stream", "Function", "WBit", "Structure" },
            updated.Element.Elements().Select(element => element.Name.LocalName).ToArray());
    }

    [TestMethod]
    public void ApplySml_HeaderWithoutItemsEmptiesTheStructure()
    {
        var library = Sample();

        var updated = library.ApplySml(library.Messages[2], Parse("S1F99 W."));

        Assert.AreEqual(99, updated.Function);
        Assert.IsTrue(updated.WBit);
        Assert.IsNotNull(updated.Structure);
        Assert.IsFalse(updated.Structure!.HasElements);
    }

    [TestMethod]
    public void ApplySml_KeepsTheRestOfTheLibraryByteFaithful()
    {
        var library = Sample();
        library.ApplySml(library.Messages[0], Parse("S2F41 W\n<L\n  <A \"PP\">\n>."));

        var reloaded = MessageLibrary.Load(library.Export());

        Assert.HasCount(3, reloaded.Messages);
        var recipe = reloaded.Messages[2].Structure!
            .Descendants("A")
            .Single(element => element.Attribute("Name")?.Value == "PPBODY");
        Assert.AreEqual("[Section1]\nKey1=1\nKey2=two\n", recipe.Element("Value")!.Value);
        Assert.AreEqual("keep", recipe.Attribute("ExtraAttr")!.Value);
        Assert.IsTrue(reloaded.Document.DescendantNodes().OfType<XComment>().Any());
        Assert.IsNotNull(reloaded.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry"));
    }
}
