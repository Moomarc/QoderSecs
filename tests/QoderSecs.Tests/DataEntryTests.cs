using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class DataEntryTests
{
    /// <summary>照 SecsExpress 产出的形状写：空字典是两行，不写自闭合。</summary>
    private const string LibraryWithEmptyDictionary = """
        <SXML>
            <DataDictionary>
            </DataDictionary>
            <MessageDictionary>
                <Message Name="S1F1_AreYouThere" Description="">
                    <Stream>1</Stream>
                    <Function>1</Function>
                    <Structure>
                        <L Name="sample list">
                            <A Name="MDLN">
                                <Value>QODERSECS</Value>
                            </A>
                            <U4 Name="SVID">
                                <Maps Type="U4">
                                    <Value>1</Value>
                                    <Value>2</Value>
                                </Maps>
                                <Value>10101</Value>
                            </U4>
                        </L>
                    </Structure>
                </Message>
            </MessageDictionary>
        </SXML>
        """;

    private const string LibraryWithoutDictionary = """
        <SXML>
            <!-- 这一份没有 DataDictionary 那一段 -->
            <MessageDictionary>
                <Message Name="S1F1">
                    <Stream>1</Stream>
                    <Function>1</Function>
                    <Structure>
                        <L Name="root list">
                            <A Name="RCMD">
                                <Value>TEST</Value>
                            </A>
                        </L>
                    </Structure>
                </Message>
            </MessageDictionary>
        </SXML>
        """;

    private static string ReadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml"));

    /// <summary>行尾跟着源文件走，比对排版时先统一成 \n，别让一个测试数出两种换行。</summary>
    private static string Flat(string xml) => xml.Replace("\r\n", "\n");

    private static XElement SampleNode(MessageLibrary library) =>
        library.Messages[0].Structure!.Element("L")!;

    [TestMethod]
    public void EmptyDictionarySectionReadsAsNoEntries()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);

        Assert.IsEmpty(library.DataEntries);
    }

    [TestMethod]
    public void MissingDictionarySectionReadsAsNoEntries()
    {
        var library = MessageLibrary.Load(LibraryWithoutDictionary);

        Assert.IsEmpty(library.DataEntries);
    }

    [TestMethod]
    public void SaveEntry_CarriesTheWholeSubtreeIncludingMaps()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);

        var entry = library.SaveEntry("sample list", SampleNode(library));

        Assert.AreEqual("sample list", entry.Name);
        Assert.AreEqual("L", entry.TypeLabel);
        Assert.AreEqual("2 项", entry.Preview);
        Assert.HasCount(1, library.DataEntries);
        Assert.AreEqual("10101", entry.Node.Children[1].SingleValue);
        Assert.IsNotNull(entry.Node.Children[1].Element.Element("Maps"));
    }

    /// <summary>条目里那个 Name 是条目自己的名字，不是源节点原来带的名字。</summary>
    [TestMethod]
    public void SaveEntry_RenamesTheEntryWithoutTouchingTheSourceNode()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);

        var entry = library.SaveEntry("stored", SampleNode(library));

        Assert.AreEqual("stored", entry.Name);
        Assert.AreEqual("sample list", SampleNode(library).Attribute("Name")?.Value);
    }

    /// <summary>存进去的必须是另一份：之后消息里那个节点改了，条目不能跟着变。</summary>
    [TestMethod]
    public void SaveEntry_IsADeepCopySoTheMessageAndTheEntryPartWays()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var entry = library.SaveEntry("sample list", SampleNode(library));
        var sourceItem = SampleNode(library).Elements().First();

        Assert.IsTrue(StructureEditor.SetValue(sourceItem, "CHANGED"));

        // 这里读条目的活节点，不读 StructureNode 快照：快照在构造时就把值拍下来了，
        // 浅拷贝跟消息共用同一批子节点时它照样显示 QODERSECS，测不出真假。
        Assert.AreEqual("QODERSECS", entry.Element.Descendants("Value").First().Value);
        Assert.AreEqual("CHANGED", sourceItem.Element("Value")!.Value);
    }

    [TestMethod]
    public void SaveEntry_KeepsSecsExpressIndentationForTheFirstEntry()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);

        library.SaveEntry("sample list", SampleNode(library));

        var exported = Flat(library.Export());
        StringAssert.Contains(exported, "\n    <DataDictionary>\n        <L Name=\"sample list\">");
        StringAssert.Contains(exported, "\n    </DataDictionary>\n    <MessageDictionary>");
        // 条目之间和收尾标签前都不能多出成片空行。
        Assert.IsFalse(exported.Split('\n').Any(line => line.Trim().Length == 0));
    }

    [TestMethod]
    public void SaveEntry_AppendsFurtherEntriesAtEightSpaces()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var list = SampleNode(library);
        library.SaveEntry("sample list", list);

        library.SaveEntry("RCMD", list.Elements().First());

        Assert.HasCount(2, library.DataEntries);
        Assert.AreEqual("sample list", library.DataEntries[0].Name);
        Assert.AreEqual("A", library.DataEntries[1].TypeLabel);
        Assert.AreEqual("RCMD", library.DataEntries[1].Name);
        var exported = Flat(library.Export());
        StringAssert.Contains(exported, "\n        <A Name=\"RCMD\">");
        StringAssert.Contains(exported, "\n    </DataDictionary>");
        Assert.IsFalse(exported.Split('\n').Any(line => line.Trim().Length == 0));
    }

    /// <summary>
    /// 条目内部沿用它在消息里那一层的排版空白，不重排。
    /// 重排要先分辨哪些空白是排版、哪些是 &lt;Value&gt; 里的内容（样本里 PPBODY 的值就带换行），
    /// 数错一处就是把用户的数据改了，所以保真优先、好看排后面。
    /// </summary>
    [TestMethod]
    public void EntryKeepsTheInsideIndentationItHadInTheMessage()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var sourceLine = Flat(LibraryWithEmptyDictionary).Split('\n')
            .First(line => line.Contains("<Value>QODERSECS</Value>"));

        library.SaveEntry("sample list", SampleNode(library));

        StringAssert.Contains(Flat(library.Export()), sourceLine);
    }

    /// <summary>
    /// 名字不是主键：撞名的两条都要留得下，各按存进去的次序排在末尾。
    /// 「一个元素既在数据字典里，也在结构树中」是很常见的情况。
    /// </summary>
    [TestMethod]
    public void SaveEntry_WithTheSameNameStartsANewEntry()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var list = SampleNode(library);
        library.SaveEntry("first", list);
        library.SaveEntry("second", list.Elements().First());

        library.SaveEntry("first", list.Elements().First());

        Assert.HasCount(3, library.DataEntries);
        Assert.AreEqual("first", library.DataEntries[0].Name);
        Assert.AreEqual("L", library.DataEntries[0].TypeLabel);
        Assert.AreEqual("second", library.DataEntries[1].Name);
        Assert.AreEqual("first", library.DataEntries[2].Name);
        Assert.AreNotSame(library.DataEntries[0].Element, library.DataEntries[2].Element);
    }

    [TestMethod]
    public void SaveEntry_CreatesTheSectionBeforeMessagesWhenTheFileHasNone()
    {
        var library = MessageLibrary.Load(LibraryWithoutDictionary);

        var entry = library.SaveEntry("RCMD", library.Messages[0].Structure!.Element("L")!.Elements().First());

        var root = library.Document.Root!;
        var sections = root.Elements().Select(element => element.Name.LocalName).ToList();
        Assert.AreEqual("DataDictionary", sections[0]);
        Assert.AreEqual("MessageDictionary", sections[1]);
        Assert.AreEqual("RCMD", entry.Name);
        // 注释是保真内容，补出这一段不能把它挤掉或挪位。
        Assert.IsTrue(root.Nodes().OfType<XComment>().Any());
        StringAssert.Contains(
            Flat(library.Export()),
            "\n    <DataDictionary>\n        <A Name=\"RCMD\">");
        StringAssert.Contains(
            Flat(library.Export()),
            "\n    </DataDictionary>\n    <MessageDictionary>");

        var reloaded = MessageLibrary.Load(library.Export());
        Assert.HasCount(1, reloaded.DataEntries);
        Assert.HasCount(1, reloaded.Messages);
    }

    /// <summary>手写成自闭合的字典段也补成两行，不能让条目贴在标签后面。</summary>
    [TestMethod]
    public void SelfClosedDictionaryGetsTheTwoLineShape()
    {
        const string xml = """
            <SXML>
                <DataDictionary />
                <MessageDictionary>
                    <Message Name="S1F1">
                        <Stream>1</Stream>
                        <Function>1</Function>
                        <Structure>
                            <A Name="RCMD">
                                <Value>TEST</Value>
                            </A>
                        </Structure>
                    </Message>
                </MessageDictionary>
            </SXML>
            """;
        var library = MessageLibrary.Load(xml);

        library.SaveEntry("rcmd", library.Messages[0].Structure!.Element("A")!);

        var exported = Flat(library.Export());
        StringAssert.Contains(exported, "\n    <DataDictionary>\n        <A Name=\"rcmd\">");
        StringAssert.Contains(exported, "\n    </DataDictionary>\n    <MessageDictionary>");
        Assert.HasCount(1, MessageLibrary.Load(exported).DataEntries);
    }

    /// <summary>改名不查重：撞了别的条目也照样改，两条同名并存。</summary>
    [TestMethod]
    public void RenamingAnEntry_OntoANameAnotherEntryHasIsAllowed()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var list = SampleNode(library);
        var first = library.SaveEntry("first", list);
        library.SaveEntry("second", list.Elements().First());

        var renamed = library.RenameEntry(first, " second ");

        Assert.HasCount(2, library.DataEntries);
        Assert.AreEqual("second", library.DataEntries[0].Name);
        Assert.AreEqual("second", renamed.Name);
    }

    /// <summary>改名可以改成空：Name 属性去掉，条目还在原位、内容不动。</summary>
    [TestMethod]
    public void RenamingAnEntry_CanClearTheNameAndKeepsTheEntry()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var entry = library.SaveEntry("first", SampleNode(library));

        Assert.AreEqual("first", library.RenameEntry(entry, "first").Name);
        Assert.AreEqual("renamed", library.RenameEntry(entry, " renamed ").Name);

        var cleared = library.RenameEntry(entry, "   ");

        Assert.AreEqual(string.Empty, cleared.Name);
        Assert.IsNull(cleared.Element.Attribute("Name"));
        Assert.HasCount(2, cleared.Element.Elements(), "清空名字不该连子项一起动");
        Assert.ThrowsExactly<ArgumentNullException>(() => library.RenameEntry(entry, null!));
        Assert.HasCount(1, library.DataEntries);
    }

    [TestMethod]
    public void RemoveEntry_TakesItsOwnIndentationWithIt()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var list = SampleNode(library);
        var first = library.SaveEntry("first", list);
        library.SaveEntry("second", list.Elements().First());

        library.RemoveEntry(first);

        Assert.HasCount(1, library.DataEntries);
        Assert.AreEqual("second", library.DataEntries[0].Name);
        StringAssert.Contains(
            Flat(library.Export()),
            "\n    <DataDictionary>\n        <A Name=\"second\">");
        Assert.IsFalse(Flat(library.Export()).Split('\n').Any(line => line.Trim().Length == 0));
    }

    /// <summary>删空之后留成 SecsExpress 那种两行写法，不塌成自闭合。</summary>
    [TestMethod]
    public void RemoveEntry_LeavesTheSameEmptyShapeTheFileStartedWith()
    {
        var original = Flat(LibraryWithEmptyDictionary);
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var list = SampleNode(library);
        var first = library.SaveEntry("first", list);
        var second = library.SaveEntry("second", list.Elements().First());

        library.RemoveEntry(first);
        library.RemoveEntry(second);

        Assert.IsEmpty(library.DataEntries);
        StringAssert.Contains(
            Flat(library.Export()),
            "\n    <DataDictionary>\n    </DataDictionary>\n    <MessageDictionary>");
        StringAssert.StartsWith(Flat(library.Export()), original.Split('\n')[0]);
    }

    /// <summary>旧快照（同一条删两次、或者来自别的库）不能再拿来定位，和 RemoveMessage 同一条防线。</summary>
    [TestMethod]
    public void StaleEntrySnapshotsAreRejectedWithoutBreakingEitherLibrary()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var entry = library.SaveEntry("first", SampleNode(library));
        var other = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var foreign = other.SaveEntry("other", SampleNode(other));
        library.RemoveEntry(entry);

        Assert.ThrowsExactly<ArgumentException>(() => library.RenameEntry(entry, "again"));
        Assert.ThrowsExactly<ArgumentException>(() => library.RemoveEntry(entry));
        Assert.ThrowsExactly<ArgumentException>(() => library.RemoveEntry(foreign));
        Assert.ThrowsExactly<ArgumentException>(() => library.RenameEntry(foreign, "mine"));
        Assert.IsEmpty(library.DataEntries);
        Assert.HasCount(1, other.DataEntries);
    }

    [TestMethod]
    public void SaveEntry_RejectsNullAndDetachedElementsButNotAnEmptyName()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var detached = new XElement("L", new XElement("Value", "1"));

        var unnamed = library.SaveEntry("  ", SampleNode(library));

        Assert.AreEqual(string.Empty, unnamed.Name);
        Assert.IsNull(unnamed.Element.Attribute("Name"), "空名字是把 Name 属性去掉，不是写一个空串进去");
        Assert.ThrowsExactly<ArgumentNullException>(() => library.SaveEntry("x", null!));
        Assert.ThrowsExactly<ArgumentException>(() => library.SaveEntry("x", detached));
        Assert.ThrowsExactly<ArgumentNullException>(() => library.RemoveEntry(null!));
        Assert.HasCount(1, library.DataEntries);
        // 拦下来的那两次不能留下半截条目：字典里就只有那一条没名字的 L（8 空格那一层）。
        StringAssert.Contains(Flat(library.Export()), "\n        <L>\n");
    }

    /// <summary>插入到消息走功能三那套落点判断；这里验的是用户裁的「复制，拖进去就断开」。</summary>
    [TestMethod]
    public void EntryClonedIntoAMessageBecomesAnIndependentCopy()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var entry = library.SaveEntry("sample list", SampleNode(library));
        var target = SampleNode(library);

        var pasted = StructureEditor.PasteInto(StructureEditor.CloneSubtree(entry.Element), target);

        Assert.IsNotNull(pasted);
        Assert.AreNotSame(entry.Element, pasted);
        Assert.HasCount(3, target.Elements().ToList());
        Assert.IsTrue(StructureEditor.SetValue(pasted!.Elements().First(), "CHANGED"));

        Assert.AreEqual("QODERSECS", entry.Element.Descendants("Value").First().Value);
        Assert.HasCount(2, entry.Element.Elements().ToList());
    }

    /// <summary>字典里那个看不懂的条目也进列表，但它的内容一个字都不能变。</summary>
    [TestMethod]
    public void UnknownDictionaryChildrenAreListedAndSurviveRoundTrip()
    {
        var library = MessageLibrary.Load(ReadFixture());

        Assert.HasCount(1, library.DataEntries);
        Assert.AreEqual("UnknownEntry", library.DataEntries[0].TypeLabel);
        Assert.IsFalse(library.DataEntries[0].Node.HasKnownType);

        var source = library.Messages
            .First(message => message.Structure?.HasElements == true)
            .Structure!.Elements().First();
        library.SaveEntry("sample list", source);

        var reloaded = MessageLibrary.Load(library.Export());
        var dictionary = reloaded.Document.Root!.Element("DataDictionary")!;
        Assert.HasCount(3, reloaded.Messages);
        Assert.HasCount(2, reloaded.DataEntries);
        Assert.AreEqual("keep me", dictionary.Element("UnknownEntry")!.Value);
        Assert.AreEqual("sample list", dictionary.Element("L")!.Attribute("Name")?.Value);
        Assert.AreEqual("sample list", reloaded.DataEntries[1].Name);
        Assert.IsTrue(reloaded.Document.DescendantNodes().OfType<XComment>().Any());
        Assert.HasCount(7, reloaded.Document.Root!.Element("MessageDictionary")!.Descendants("Maps").ToList());
    }

    /// <summary>多行的配方正文折成一排显示；这一栏只给人认条目，文档里那几行值不动。</summary>
    [TestMethod]
    public void PreviewFlattensLineBreaksAndLeavesTheDocumentAlone()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var body = library.Messages[2].Structure!.Descendants("A")
            .Single(element => element.Attribute("Name")?.Value == "PPBODY");

        var entry = library.SaveEntry("body", body);

        Assert.AreEqual("[Section1] Key1=1 Key2=two", entry.Preview);
        Assert.AreEqual("keep", entry.Element.Attribute("ExtraAttr")?.Value);
        StringAssert.StartsWith(entry.AutomationName, "body A ");
        StringAssert.Contains(Flat(library.Export()), "[Section1]\nKey1=1\nKey2=two\n");
    }

    [TestMethod]
    public void PreviewTrimsALongValueAndMarksIt()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var item = SampleNode(library).Elements().First();
        var longValue = string.Concat(Enumerable.Range(0, 10).Select(_ => "0123456789"));
        Assert.IsTrue(StructureEditor.SetValue(item, longValue));

        var entry = library.SaveEntry("long", item);

        Assert.AreEqual($"{longValue[..60]}…", entry.Preview);
    }

    /// <summary>B 和 Boolean 这种带自己写法的值也要整份进得来，预览读得出原样。</summary>
    [TestMethod]
    public void SaveEntry_CarriesBinaryAndBooleanValues()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var list = library.Messages[2].Structure!.Element("L")!;

        var mask = library.SaveEntry("mask", list.Element("B")!);
        var flag = library.SaveEntry("flag", list.Element("Boolean")!);

        // 样本库的字典里原本就有一个看不懂的条目，所以这里是 3 个而不是 2 个。
        Assert.HasCount(3, library.DataEntries);
        Assert.AreEqual("B", mask.TypeLabel);
        Assert.AreEqual("00FF", mask.Preview);
        Assert.AreEqual("True", flag.Preview);
    }

    /// <summary>
    /// 「新增数据」建出来的空条目：内容用 <see cref="StructureEditor.NewElement"/> 那套
    /// 这个类型必然合法的默认值，和结构页「添加子项」同一档，刚建出来不该是红的。
    /// </summary>
    [TestMethod]
    public void NewEntry_BuildsEachTypeWithAValidDefault()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);

        var list = library.NewEntry("清单", SecsItemType.List);
        var text = library.NewEntry("RCMD", SecsItemType.A);
        var number = library.NewEntry("SVID", SecsItemType.U4);
        var binary = library.NewEntry("掩码", SecsItemType.B);
        var flag = library.NewEntry("开关", SecsItemType.Boolean);
        var real = library.NewEntry("系数", SecsItemType.F8);

        Assert.HasCount(6, library.DataEntries);
        Assert.AreEqual("L", list.TypeLabel);
        Assert.IsEmpty(list.Node.Children);
        Assert.AreEqual("A", text.TypeLabel);
        Assert.AreEqual(string.Empty, text.Preview);
        Assert.AreEqual("U4", number.TypeLabel);
        Assert.AreEqual("0", number.Preview);
        Assert.AreEqual("B", binary.TypeLabel);
        Assert.AreEqual("Boolean", flag.TypeLabel);
        Assert.AreEqual("False", flag.Preview);
        Assert.AreEqual("F8", real.TypeLabel);
        Assert.AreEqual("0", real.Preview);

        // 每一个都发得出去，不然建出来的素材还要人先补一遍。
        foreach (var entry in library.DataEntries)
        {
            Assert.IsTrue(entry.Node.HasKnownType, $"「{entry.Name}」的类型认不出来。");
        }
    }

    /// <summary>名字可空是数据元素的规则（SecsExpress 实测）：留空就是没有 Name 属性。</summary>
    [TestMethod]
    public void NewEntry_AllowsAnEmptyNameButNotAMissingOne()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);

        var unnamed = library.NewEntry("   ", SecsItemType.U4);

        Assert.AreEqual(string.Empty, unnamed.Name);
        Assert.IsNull(unnamed.Element.Attribute("Name"));
        Assert.ThrowsExactly<ArgumentNullException>(() => library.NewEntry(null!, SecsItemType.U4));

        var reloaded = MessageLibrary.Load(library.Export());
        Assert.HasCount(1, reloaded.DataEntries);
        Assert.AreEqual("U4", reloaded.DataEntries[0].TypeLabel);
        Assert.AreEqual("0", reloaded.DataEntries[0].Preview);
    }

    /// <summary>名字不是主键：同一个名字连建两次，两条都在。</summary>
    [TestMethod]
    public void NewEntry_KeepsBothEntriesWhenTheNameIsAlreadyUsed()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);

        var first = library.NewEntry("SVID", SecsItemType.U4);
        var second = library.NewEntry("SVID", SecsItemType.U4);

        Assert.HasCount(2, library.DataEntries);
        Assert.AreEqual("SVID", first.Name);
        Assert.AreEqual("SVID", second.Name);
        Assert.AreNotSame(first.Element, second.Element);
        Assert.AreEqual(0, first.Index);
        Assert.AreEqual(1, second.Index);
    }

    /// <summary>选中一条再点「新增数据」：新条目落在它后面，不是跑到末尾。</summary>
    [TestMethod]
    public void NewEntry_GoesRightAfterTheEntryItWasPointedAt()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        library.NewEntry("第一条", SecsItemType.U4);
        var second = library.NewEntry("第二条", SecsItemType.U4);
        library.NewEntry("第三条", SecsItemType.U4);

        var inserted = library.NewEntry("插进来", SecsItemType.A, second);

        Assert.AreEqual(2, inserted.Index);
        CollectionAssert.AreEqual(
            new[] { "第一条", "第二条", "插进来", "第三条" },
            library.DataEntries.Select(entry => entry.Name).ToList());
    }

    /// <summary>换过库或者删过的旧快照不能再当落点，和 RemoveEntry 同一条防线。</summary>
    [TestMethod]
    public void NewEntry_RejectsAStalePointer()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var other = MessageLibrary.Load(LibraryWithEmptyDictionary);
        var foreign = other.NewEntry("别处的", SecsItemType.U4);
        var removed = library.NewEntry("删掉的", SecsItemType.U4);
        library.RemoveEntry(removed);

        Assert.ThrowsExactly<ArgumentException>(() => library.NewEntry("x", SecsItemType.U4, foreign));
        Assert.ThrowsExactly<ArgumentException>(() => library.NewEntry("x", SecsItemType.U4, removed));
        // 拦下来时那一条已经删掉的不能自己回来，也不能留下半截新条目。
        Assert.IsEmpty(library.DataEntries);
        Assert.HasCount(1, other.DataEntries);
    }

    /// <summary>新建的条目排版照 SecsExpress：条目 8 空格、收尾标签 4 空格，不留成片空行。</summary>
    [TestMethod]
    public void NewEntry_KeepsSecsExpressIndentation()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        library.NewEntry("SVID", SecsItemType.U4);

        library.NewEntry("CEID", SecsItemType.U4);

        var exported = Flat(library.Export());
        StringAssert.Contains(exported, "\n    <DataDictionary>\n        <U4 Name=\"SVID\">");
        StringAssert.Contains(exported, "\n        <U4 Name=\"CEID\">");
        StringAssert.Contains(exported, "\n    </DataDictionary>\n    <MessageDictionary>");
        Assert.IsFalse(exported.Split('\n').Any(line => line.Trim().Length == 0));
    }

    [TestMethod]
    public void NewEntry_CreatesTheSectionWhenTheFileHasNone()
    {
        var library = MessageLibrary.Load(LibraryWithoutDictionary);

        var entry = library.NewEntry("SVID", SecsItemType.U4);

        var sections = library.Document.Root!.Elements().Select(element => element.Name.LocalName).ToList();
        Assert.AreEqual("DataDictionary", sections[0]);
        Assert.AreEqual("MessageDictionary", sections[1]);
        StringAssert.Contains(
            Flat(library.Export()),
            "\n    <DataDictionary>\n        <U4 Name=\"SVID\">");

        var reloaded = MessageLibrary.Load(library.Export());
        Assert.HasCount(1, reloaded.DataEntries);
        Assert.AreEqual("0", reloaded.DataEntries[0].Preview);
    }

    /// <summary>新建、改名、改内部值都活在导出文件里；注释、未知条目和 Maps 这些保真内容不动。</summary>
    [TestMethod]
    public void NewEntriesAndEditsInsideThemSurviveAnExportReload()
    {
        var library = MessageLibrary.Load(ReadFixture());
        var list = library.NewEntry("报告", SecsItemType.List);
        var svid = StructureEditor.AddChild(list.Element, SecsItemType.U4);
        Assert.IsNotNull(svid);
        Assert.IsTrue(StructureEditor.SetValue(svid!, "10001"));
        library.RenameEntry(list, "ReportList");

        var reloaded = MessageLibrary.Load(library.Export());

        // 字典里那个看不懂的条目排在新条目前面，它一个字都没变。
        Assert.HasCount(2, reloaded.DataEntries);
        Assert.AreEqual("UnknownEntry", reloaded.DataEntries[0].TypeLabel);
        Assert.AreEqual("ReportList", reloaded.DataEntries[1].Name);
        Assert.AreEqual("10001", reloaded.DataEntries[1].Node.Children[0].SingleValue);
        Assert.HasCount(3, reloaded.Messages);
        Assert.IsTrue(reloaded.Document.DescendantNodes().OfType<XComment>().Any());
        Assert.HasCount(7, reloaded.Document.Root!.Element("MessageDictionary")!.Descendants("Maps").ToList());
    }

    [TestMethod]
    public void ClearAlsoClearsTheEntries()
    {
        var library = MessageLibrary.Load(LibraryWithEmptyDictionary);
        library.SaveEntry("first", SampleNode(library));

        library.Clear();

        Assert.IsEmpty(library.DataEntries);
    }
}
