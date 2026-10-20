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
        Assert.AreEqual("W", first.WBitMark);

        var second = library.Messages[1];
        Assert.IsFalse(second.WBit);
        Assert.AreEqual("-", second.WBitMark);
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

    /// <summary>
    /// SecsExpress 只认带 <c>&lt;DataDictionary&gt;</c> 段的文件——单变量对照件实测过，
    /// 六个变体里只有删掉这一段的那份和我们的样例库打不开。这一段是导出的硬条件，
    /// 缺了就在读的时候补上，不能等到存文件。
    /// </summary>
    [TestMethod]
    public void Load_AddsTheDataDictionarySectionWhenTheFileLacksIt()
    {
        const string xml = """
            <SXML>
                <MessageDictionary>
                    <Message Name="S1F1">
                        <Stream>1</Stream>
                        <Function>1</Function>
                        <Structure />
                    </Message>
                </MessageDictionary>
            </SXML>
            """;
        var library = MessageLibrary.Load(xml);

        var exported = library.Export();
        Assert.IsEmpty(library.DataEntries);
        Assert.IsFalse(
            exported.Contains("<DataDictionary />", StringComparison.Ordinal),
            "补出来的要照 SecsExpress 的两行写法，不能塌成自闭合。");

        var root = library.Document.Root!;
        Assert.AreEqual("DataDictionary", root.Elements().First().Name.LocalName, "字典段得在消息段之前。");
        Assert.IsFalse(root.Element("DataDictionary")!.IsEmpty);

        // 再读一遍不该又长出第二段，也不该改一个字节。
        var reloaded = MessageLibrary.Load(exported);
        Assert.HasCount(1, reloaded.Document.Root!.Elements("DataDictionary"));
        Assert.AreEqual(exported, reloaded.Export());
    }

    [TestMethod]
    public void ANewLibraryFromScratchExportsThatSectionToo()
    {
        var library = MessageLibrary.Empty;

        StringAssert.Contains(library.Export(), "<DataDictionary>");

        library.NewMessage(1, 1, "S1F1");
        var reloaded = MessageLibrary.Load(library.Export());

        Assert.HasCount(1, reloaded.Messages);
        Assert.IsEmpty(reloaded.DataEntries);
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

    /// <summary>
    /// 消息名的两条规则：非空、且不许多条共用。这是 SecsExpress 对消息体的硬要求
    /// （SecsExpress 实测），反过来 SxFy 可以重复。
    /// </summary>
    [TestMethod]
    public void SetName_RejectsBlankAndNamesAnotherMessageAlreadyHas()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var first = library.Messages[0];
        var otherName = library.Messages[1].Name;
        var exported = library.Export();

        Assert.ThrowsExactly<ArgumentException>(() => library.SetName(first, "   "));
        Assert.ThrowsExactly<ArgumentException>(() => library.SetName(first, otherName));
        Assert.AreEqual(exported, library.Export(), "拒掉之后文档一个字都不该变");

        // 改回自己原来那个名字不算撞自己。
        Assert.AreEqual(first.Name, library.SetName(first, first.Name).Name);
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

    [TestMethod]
    public void RemoveMessage_MiddleEntryShiftsLaterIndices()
    {
        var library = MessageLibrary.Load(ReadFixture());

        library.RemoveMessage(library.Messages[1]);

        // Index 是位置，删一条后面全部前移；旧快照的 Index 从这里开始就不能再用了。
        Assert.HasCount(2, library.Messages);
        Assert.AreEqual("S1F1_AreYouThere", library.Messages[0].Name);
        Assert.AreEqual(0, library.Messages[0].Index);
        Assert.AreEqual("RecipeBody", library.Messages[1].Name);
        Assert.AreEqual(1, library.Messages[1].Index);
    }

    [TestMethod]
    public void RemoveMessage_LeavesNoBlankLineBehind()
    {
        var library = MessageLibrary.Load(ReadFixture());

        library.RemoveMessage(library.Messages[1]);
        var exported = library.Export();

        // 换行缩进要跟消息一起走，否则删除处会留下成片空行，文本工具里比对就是整片不同。
        var lines = exported.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            Assert.IsFalse(
                lines[i].Trim().Length == 0 && lines[i - 1].Trim().Length == 0,
                $"第 {i + 1} 行连着上一行都是空行：{Environment.NewLine}{exported}");
        }

        Assert.HasCount(2, library.Document.Descendants("Message").ToList());
    }

    /// <summary>
    /// 口径是置空，不是只提示：目标都删了，指着它的引用
    /// 留在文件里也指不到，界面上还看不出来，所以整个 AutoReply 元素一起删掉。
    /// </summary>
    [TestMethod]
    public void RemoveMessage_ClearsThePointersThatBreak()
    {
        var library = MessageLibrary.Load(ReadFixture());

        // 被指向的是 S1F2_OnlineData。
        library.RemoveMessage(library.Messages[1]);

        Assert.IsNull(library.Messages[0].AutoReply);
        Assert.IsNull(AutoReplyResolver.FindTarget(library, 1, 1));
        Assert.IsFalse(
            library.Export().Contains("<AutoReply></AutoReply>", StringComparison.Ordinal),
            "置空是把元素删掉，不是留一个空壳");
    }

    [TestMethod]
    public void RemoveMessage_KeepsTheRestOfTheDocumentLoadable()
    {
        var library = MessageLibrary.Load(ReadFixture());

        library.RemoveMessage(library.Messages[0]);
        var reloaded = MessageLibrary.Load(library.Export());

        Assert.HasCount(2, reloaded.Messages);
        Assert.AreEqual("S1F2_OnlineData", reloaded.Messages[0].Name);
        Assert.AreEqual("RecipeBody", reloaded.Messages[1].Name);

        // 未知内容原样保留：DataDictionary、注释、Maps。
        Assert.AreEqual("keep me", reloaded.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry")!.Value);
        Assert.IsTrue(reloaded.Document.DescendantNodes().OfType<XComment>().Any());
        Assert.HasCount(7, reloaded.Document.Descendants("Maps").ToList());
    }

    [TestMethod]
    public void RemoveMessage_LastEntryLeavesAnEmptyDictionaryThatStillGrows()
    {
        var library = MessageLibrary.Load(ReadFixture());
        while (library.Messages.Count > 0)
        {
            library.RemoveMessage(library.Messages[^1]);
        }

        Assert.IsEmpty(library.Messages);
        Assert.IsFalse(library.Document.Root!.Element("MessageDictionary")!.Nodes().Any());

        library.NewMessage(2, 3, "S2F3_New");
        var reloaded = MessageLibrary.Load(library.Export());
        Assert.HasCount(1, reloaded.Messages);
        Assert.AreEqual("S2F3", reloaded.Messages[0].Signature);
        Assert.AreEqual("S2F3_New", reloaded.Messages[0].Name);
    }

    [TestMethod]
    public void RemoveMessage_RejectsStaleAndForeignEntries()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[0];
        var foreign = MessageLibrary.Load(ReadFixture()).Messages[1];

        Assert.ThrowsExactly<ArgumentException>(() => library.RemoveMessage(foreign));
        Assert.HasCount(3, library.Messages);

        library.RemoveMessage(entry);
        // 同一条删两次：抛出来，且不能再动别的消息。
        Assert.ThrowsExactly<ArgumentException>(() => library.RemoveMessage(entry));
        Assert.HasCount(2, library.Messages);
        Assert.ThrowsExactly<ArgumentNullException>(() => library.RemoveMessage(null!));
    }

    // ---- v1.1 m3 复制消息 ----

    [TestMethod]
    public void DuplicateMessage_CopiesEverythingExceptTheName()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[0];

        var copy = library.DuplicateMessage(entry, "S1F1_AreYouThere Copy");

        Assert.AreEqual("S1F1_AreYouThere Copy", copy.Name);
        Assert.AreEqual("S1F1", copy.Signature);
        Assert.IsTrue(copy.WBit);
        Assert.AreEqual("S1F2_OnlineData", copy.AutoReply);
        Assert.AreEqual("link test", copy.Element.Attribute("Description")?.Value);
        Assert.IsNotNull(copy.Structure);
        Assert.IsFalse(copy.Structure!.HasElements);

        // 副本落在原条后面一格，原条之后的全部后移一位。
        Assert.HasCount(4, library.Messages);
        Assert.AreEqual(0, library.Messages[0].Index);
        Assert.AreEqual("S1F1_AreYouThere", library.Messages[0].Name);
        Assert.AreEqual(1, copy.Index);
        Assert.AreEqual("S1F2_OnlineData", library.Messages[2].Name);
        Assert.AreEqual("RecipeBody", library.Messages[3].Name);
    }

    [TestMethod]
    public void DuplicateMessage_CopiesTheStructureBodyAndChildOrder()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[2];

        var copy = library.DuplicateMessage(entry, "RecipeBody Copy");

        CollectionAssert.AreEqual(ChildOrder(entry.Element), ChildOrder(copy.Element));
        Assert.HasCount(entry.Element.Descendants("Maps").Count(), copy.Element.Descendants("Maps").ToList());

        var originalBody = entry.Structure!.Descendants("A")
            .Single(element => element.Attribute("Name")?.Value == "PPBODY");
        var copiedBody = copy.Structure!.Descendants("A")
            .Single(element => element.Attribute("Name")?.Value == "PPBODY");
        Assert.AreEqual("keep", copiedBody.Attribute("ExtraAttr")?.Value);
        Assert.AreEqual(originalBody.Element("Value")!.Value, copiedBody.Element("Value")!.Value);
    }

    [TestMethod]
    public void DuplicateMessage_IsADeepCloneSoEditingTheCopyLeavesTheOriginalAlone()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[1];

        var copy = library.DuplicateMessage(entry, "S1F2_OnlineData Copy");

        // 走库的改法，不走直接写 XML：MessageEntry 是一次性快照，手写的改动不会刷进手上那条的 Signature。
        var edited = library.SetHeader(copy, 9, 9, false);
        edited.Element.Element("Structure")!.Descendants("A").First().Element("Value")!.Value = "CHANGED";

        var original = library.Messages.Single(message => message.Name == "S1F2_OnlineData");
        Assert.AreEqual("S1F2", original.Signature);
        Assert.AreEqual("QODERSECS", original.Structure!.Descendants("A").First().Element("Value")!.Value);
        Assert.AreEqual("S9F9", edited.Signature);
        Assert.AreEqual("CHANGED", edited.Structure!.Descendants("A").First().Element("Value")!.Value);
        Assert.AreSame(entry.Element, original.Element);
    }

    /// <summary>副本的名字同样受那两条规则约束：空不行、撞别的消息也不行，拦下来不留半截副本。</summary>
    [TestMethod]
    public void DuplicateMessage_RejectsBlankAndDuplicateNames()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var exported = library.Export();

        Assert.ThrowsExactly<ArgumentException>(() => library.DuplicateMessage(library.Messages[0], "   "));
        Assert.ThrowsExactly<ArgumentException>(
            () => library.DuplicateMessage(library.Messages[0], library.Messages[1].Name));

        Assert.HasCount(3, library.Messages);
        Assert.AreEqual(exported, library.Export());
    }

    [TestMethod]
    public void NextFreeMessageName_SkipsTheNamesTheLibraryAlreadyHas()
    {
        var library = MessageLibrary.Load(ReadFixture());

        Assert.AreEqual("消息", library.NextFreeMessageName("   "), "空白的基名落一个能认出来的名字，不是报错");
        Assert.AreEqual(library.Messages[0].Name + " 2", library.NextFreeMessageName(library.Messages[0].Name));

        // 连着按两次「新建」走的就是这条路：第二次不该因为撞名而失败。
        library.NewMessage(1, 99, library.NextFreeMessageName("消息"));
        Assert.AreEqual("消息 2", library.NextFreeMessageName("消息"));
        library.NewMessage(1, 99, library.NextFreeMessageName("消息"));
        Assert.AreEqual("消息 3", library.NextFreeMessageName("消息"));
    }

    /// <summary>同一个 SxFy 摆三条、靠名字区分（同一个 S2F41 摆 START/STOP/PP_SELECT 是正常用法）。</summary>
    [TestMethod]
    public void TheSameSignatureCanRepeatButTheNameCannot()
    {
        const string xml = """
            <SXML>
                <DataDictionary>
                </DataDictionary>
                <MessageDictionary>
                    <Message Name="START">
                        <Stream>2</Stream>
                        <Function>41</Function>
                        <AutoReply>ACK_START</AutoReply>
                        <Structure />
                    </Message>
                    <Message Name="STOP">
                        <Stream>2</Stream>
                        <Function>41</Function>
                        <Structure />
                    </Message>
                    <Message Name="PP_SELECT">
                        <Stream>2</Stream>
                        <Function>41</Function>
                        <Structure />
                    </Message>
                    <Message Name="ACK_START">
                        <Stream>2</Stream>
                        <Function>42</Function>
                        <Structure />
                    </Message>
                </MessageDictionary>
            </SXML>
            """;
        var library = MessageLibrary.Load(xml);

        CollectionAssert.AreEqual(
            new[] { "START", "STOP", "PP_SELECT", "ACK_START" },
            library.Messages.Select(entry => entry.Name).ToList());
        Assert.AreEqual("S2F41", library.Messages[0].Signature);
        Assert.AreEqual("S2F41", library.Messages[2].Signature, "三条共用一个 SxFy 是合法的");

        // 再加一条同 SxFy 的：只要名字是新的就收。
        var fourth = library.NewMessage(2, 41, "EQUIP_SELECT");
        Assert.AreEqual("S2F41", fourth.Signature);
        Assert.ThrowsExactly<ArgumentException>(() => library.NewMessage(2, 41, "STOP"));

        // 同 SxFy 多条时取第一条配了 AutoReply 的，指向按名字精确落——名字唯一就是这一步不出歧义的理由。
        Assert.AreEqual("ACK_START", AutoReplyResolver.FindTarget(library, 2, 41)?.Name);
    }

    [TestMethod]
    public void DuplicateMessage_KeepsOneMessagePerLineAndNoBlankRuns()
    {
        var library = MessageLibrary.Load(ReadFixture());

        // 复制的是最后一条：它和 </MessageDictionary> 之间不能被挤成一格。
        library.DuplicateMessage(library.Messages[2], "RecipeBody Copy");
        var exported = library.Export();
        string[] lines = [.. exported.Split('\n')];

        for (var i = 1; i < lines.Length; i++)
        {
            Assert.IsFalse(
                lines[i].Trim().Length == 0 && lines[i - 1].Trim().Length == 0,
                $"第 {i + 1} 行连着上一行都是空行：{Environment.NewLine}{exported}");
        }

        var originalLine = IndexOfMessageLine(lines, "RecipeBody");
        var copyLine = IndexOfMessageLine(lines, "RecipeBody Copy");
        Assert.IsTrue(originalLine >= 0 && copyLine >= 0, $"导出里没有各自的整行消息：{Environment.NewLine}{exported}");

        // 副本单独占一行，缩进和原条一致（换行缩进是从文件里已有的节点抄来的）。
        Assert.AreEqual(
            LeadingSpaces(lines[originalLine]),
            LeadingSpaces(lines[copyLine]));
        Assert.IsTrue(lines[copyLine].TrimStart().StartsWith("<Message", StringComparison.Ordinal));

        // 最后一条消息的收尾标签必须自己占一行，</MessageDictionary> 前一行就是它。
        var closeLine = Array.FindIndex(lines, line => line.Trim() == "</MessageDictionary>");
        Assert.IsGreaterThan(0, closeLine);
        Assert.AreEqual("</Message>", lines[closeLine - 1].Trim());
        Assert.HasCount(4, library.Document.Descendants("Message").ToList());
    }

    private static int IndexOfMessageLine(string[] lines, string name) =>
        Array.FindIndex(lines, line => line.TrimStart().StartsWith($"<Message Name=\"{name}\"", StringComparison.Ordinal));

    private static int LeadingSpaces(string line) => line.Length - line.TrimStart().Length;

    [TestMethod]
    public void DuplicateMessage_KeepsTheRestOfTheDocumentLoadable()
    {
        var library = MessageLibrary.Load(ReadFixture());

        library.DuplicateMessage(library.Messages[0], "S1F1_AreYouThere Copy");
        var reloaded = MessageLibrary.Load(library.Export());

        Assert.HasCount(4, reloaded.Messages);
        Assert.AreEqual("S1F1_AreYouThere Copy", reloaded.Messages[1].Name);

        Assert.AreEqual("keep me", reloaded.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry")!.Value);
        Assert.IsTrue(reloaded.Document.DescendantNodes().OfType<XComment>().Any());
        Assert.HasCount(7, reloaded.Document.Descendants("Maps").ToList());
    }

    [TestMethod]
    public void DuplicateMessage_CopyingTheMessageThatPointsAtAnotherKeepsThePointer()
    {
        var library = MessageLibrary.Load(ReadFixture());

        library.DuplicateMessage(library.Messages[0], "S1F1_AreYouThere Copy");

        // 同 SxFy 现在有两条、都配了 AutoReply：解析器取排在前面那条的目标，两条目标相同，结果不变。
        var target = AutoReplyResolver.FindTarget(library, 1, 1);
        Assert.AreEqual("S1F2_OnlineData", target?.Name);
    }

    [TestMethod]
    public void DuplicateMessage_CopyingThePointerTargetLeavesThePointerOnTheOriginal()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[1];

        library.DuplicateMessage(entry, "S1F2_OnlineData Copy");

        var target = AutoReplyResolver.FindTarget(library, 1, 1);
        Assert.IsNotNull(target);
        Assert.AreEqual("S1F2_OnlineData", target!.Name);
        Assert.AreSame(entry.Element, target.Element);
    }

    [TestMethod]
    public void DuplicateMessage_RejectsStaleAndForeignEntries()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var entry = library.Messages[0];
        var foreign = MessageLibrary.Load(ReadFixture()).Messages[1];

        Assert.ThrowsExactly<ArgumentException>(() => library.DuplicateMessage(foreign, "X"));
        Assert.HasCount(3, library.Messages);

        library.Clear();
        // 库换过之后手上的快照不算，不能往新文档里塞旧文档的元素。
        Assert.ThrowsExactly<ArgumentException>(() => library.DuplicateMessage(entry, "X"));
        Assert.ThrowsExactly<ArgumentNullException>(() => library.DuplicateMessage(null!, "X"));
    }
}

