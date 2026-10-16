using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class StructureEditorTests
{
    private static MessageLibrary Sample() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));

    /// <summary>RecipeBody 那条消息的根 L，底下有 PPID、PPBODY、Mask、Flag、Count 五个子项。</summary>
    private static XElement Root(MessageLibrary library) =>
        StructureNode.FromStructure(library.Messages[2].Structure)!.Element;

    private static readonly SecsItemType[] Numerics =
    [
        SecsItemType.I1, SecsItemType.I2, SecsItemType.I4, SecsItemType.I8,
        SecsItemType.U1, SecsItemType.U2, SecsItemType.U4, SecsItemType.U8,
        SecsItemType.F4, SecsItemType.F8
    ];

    // ---------- 新建元素 ----------

    [TestMethod]
    public void NewElement_UsesTheCanonicalElementName()
    {
        foreach (var type in Enum.GetValues<SecsItemType>())
        {
            var element = StructureEditor.NewElement(type);
            Assert.AreEqual(SecsItemTypes.ElementName(type), element.Name.LocalName, type.ToString());
        }

        Assert.AreEqual("L", StructureEditor.NewElement(SecsItemType.List).Name.LocalName);
        Assert.AreEqual("Boolean", StructureEditor.NewElement(SecsItemType.Boolean).Name.LocalName);
    }

    [TestMethod]
    public void NewElement_GivesEveryTypeAValidStartingValue()
    {
        Assert.IsEmpty(StructureEditor.NewElement(SecsItemType.List).Elements());

        foreach (var type in new[] { SecsItemType.A, SecsItemType.J, SecsItemType.B })
        {
            var node = new StructureNode(StructureEditor.NewElement(type));
            Assert.IsNull(node.ValidationError, type.ToString());
            Assert.AreEqual(string.Empty, node.Values.Single(), type.ToString());
        }

        var boolean = new StructureNode(StructureEditor.NewElement(SecsItemType.Boolean));
        Assert.IsNull(boolean.ValidationError);
        Assert.AreEqual("False", boolean.Values.Single());

        foreach (var type in Numerics)
        {
            var node = new StructureNode(StructureEditor.NewElement(type));
            Assert.IsNull(node.ValidationError, type.ToString());
            Assert.AreEqual("0", node.SingleValue, type.ToString());
        }
    }

    [TestMethod]
    public void NewElement_OfEveryTypeKeepsTheMessageSendable()
    {
        foreach (var type in Enum.GetValues<SecsItemType>())
        {
            var library = Sample();
            Assert.IsNotNull(StructureEditor.AddChild(library.Messages[0].Structure!, type), type.ToString());
            Assert.IsTrue(
                SmlConverter.TryToSml(library.Messages[0], out _, out var error),
                $"{type}: {error}");
        }
    }

    // ---------- 添加子项 ----------

    [TestMethod]
    public void AddChild_AppendsTheChosenTypeToAList()
    {
        var root = Root(Sample());

        var added = StructureEditor.AddChild(root, SecsItemType.U4);

        Assert.IsNotNull(added);
        Assert.AreEqual("U4", added!.Name.LocalName);
        Assert.HasCount(6, root.Elements());
        Assert.AreSame(added, root.Elements().Last());
        Assert.AreEqual("0", new StructureNode(added).SingleValue);
    }

    [TestMethod]
    public void AddChild_RefusesAnythingThatIsNotAList()
    {
        var root = Root(Sample());
        var count = root.Elements().ElementAt(4);
        Assert.AreEqual("U4", count.Name.LocalName);

        Assert.IsNull(StructureEditor.AddChild(count, SecsItemType.List));
        Assert.IsNull(StructureEditor.AddChild(count.Element("Value")!, SecsItemType.List));
        Assert.IsNull(StructureEditor.AddChild(count.Element("Maps")!, SecsItemType.A));
        Assert.HasCount(5, root.Elements());
    }

    [TestMethod]
    public void AddChild_OnAnEmptyStructureCreatesTheRootItem()
    {
        var structure = Sample().Messages[0].Structure!;
        Assert.IsNull(StructureNode.FromStructure(structure));

        var added = StructureEditor.AddChild(structure, SecsItemType.List);

        Assert.IsNotNull(added);
        Assert.AreSame(added, StructureNode.FromStructure(structure)!.Element);
    }

    [TestMethod]
    public void AddChild_RefusesASecondRootItem()
    {
        var structure = Sample().Messages[1].Structure!;
        Assert.IsNotNull(StructureNode.FromStructure(structure));

        Assert.IsNull(StructureEditor.AddChild(structure, SecsItemType.List));
        Assert.HasCount(1, structure.Elements());
    }

    // ---------- 添加同级 ----------

    [TestMethod]
    public void AddSibling_InsertsRightAfterTheSelectedElement()
    {
        var root = Root(Sample());
        var ppid = root.Elements().First();
        Assert.AreEqual("PPID", ppid.Attribute("Name")!.Value);

        var added = StructureEditor.AddSibling(ppid, SecsItemType.A);

        Assert.IsNotNull(added);
        Assert.AreEqual("A", added!.Name.LocalName);
        Assert.AreSame(added, ppid.ElementsAfterSelf().First());
        Assert.HasCount(6, root.Elements());
    }

    [TestMethod]
    public void AddSibling_RefusesTheRootItem()
    {
        var library = Sample();

        Assert.IsNull(StructureEditor.AddSibling(Root(library), SecsItemType.List));
        Assert.HasCount(1, library.Messages[2].Structure!.Elements());
    }

    // ---------- 菜单可用性判断 ----------

    [TestMethod]
    public void CanAddChild_AgreesWithAddChildOnEveryPosition()
    {
        var library = Sample();
        var structure = library.Messages[0].Structure!;
        var root = Root(library);
        var leaf = root.Elements().ElementAt(4);

        var positions = new[]
        {
            structure,                                  // 空 Structure：能加，加的是根
            library.Messages[1].Structure!,             // 已有根的 Structure：不能加第二个根
            root,                                       // L：能加
            leaf,                                       // U4：不能加
            leaf.Element("Value")!,                     // Value：不能加
            leaf.Element("Maps")!                       // Maps：不能加
        };

        foreach (var position in positions)
        {
            var allowed = StructureEditor.CanAddChild(position);
            var added = StructureEditor.AddChild(position, SecsItemType.A);
            Assert.AreEqual(allowed, added is not null, position.Name.LocalName);
            added?.Remove();
        }
    }

    [TestMethod]
    public void CanAddSibling_AgreesWithAddSiblingOnRootAndChildren()
    {
        var library = Sample();
        var root = Root(library);
        var ppid = root.Elements().First();

        Assert.IsFalse(StructureEditor.CanAddSibling(root));
        Assert.IsNull(StructureEditor.AddSibling(root, SecsItemType.A));

        Assert.IsTrue(StructureEditor.CanAddSibling(ppid));
        Assert.IsNotNull(StructureEditor.AddSibling(ppid, SecsItemType.A));

        var detached = StructureEditor.NewElement(SecsItemType.U4);
        Assert.IsFalse(StructureEditor.CanAddSibling(detached), "不在树里的节点没有同级可加");
    }

    // ---------- 删除 ----------

    [TestMethod]
    public void Delete_RemovesTheElementAndItsWholeSubtree()
    {
        var root = Root(Sample());
        var mask = root.Elements().ElementAt(2);
        Assert.AreEqual("Mask", mask.Attribute("Name")!.Value);

        Assert.IsTrue(StructureEditor.Delete(mask));

        Assert.IsNull(mask.Parent);
        Assert.HasCount(4, root.Elements());
        Assert.IsFalse(root.Elements().Any(element => (string?)element.Attribute("Name") == "Mask"));
    }

    [TestMethod]
    public void Delete_OnTheRootItemEmptiesTheStructure()
    {
        var library = Sample();
        var root = Root(library);

        Assert.IsTrue(StructureEditor.Delete(root));

        Assert.IsNull(StructureNode.FromStructure(library.Messages[2].Structure));
        Assert.IsFalse(StructureEditor.Delete(root), "已经脱离树的节点不该再删一次");
    }

    // ---------- 改动落到导出 ----------

    [TestMethod]
    public void Mutations_ReachExportAndLeaveTheRestOfTheLibraryUntouched()
    {
        var library = Sample();
        var before = library.Export();

        StructureEditor.AddChild(Root(library), SecsItemType.U2);
        StructureEditor.Delete(library.Messages[1].Structure!.Elements().First());

        var after = library.Export();
        Assert.AreNotEqual(before, after);
        StringAssert.Contains(after, "<U2>");

        var reloaded = MessageLibrary.Load(after);
        Assert.HasCount(3, reloaded.Messages);
        Assert.AreEqual("S1F1_AreYouThere", reloaded.Messages[0].Name);
        Assert.IsNull(StructureNode.FromStructure(reloaded.Messages[0].Structure));
        Assert.IsNull(StructureNode.FromStructure(reloaded.Messages[1].Structure), "删掉根 L 之后结构就该是空的");
        Assert.HasCount(6, StructureNode.FromStructure(reloaded.Messages[2].Structure)!.Children);

        // 未知元素、注释和多行 Value 都得原样留着
        var unknown = reloaded.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry")!;
        Assert.AreEqual("keep me", unknown.Value);
        Assert.AreEqual("7", unknown.Attribute("Code")!.Value);
        StringAssert.Contains(after, "注释必须原样保留");
        StringAssert.Contains(after, "ExtraAttr=\"keep\"");
        Assert.AreEqual("[Section1]\nKey1=1\nKey2=two\n", reloaded.Messages[2].Structure!
            .Descendants("A").First(element => (string?)element.Attribute("Name") == "PPBODY")
            .Element("Value")!.Value.Replace("\r\n", "\n"));
    }

    // ---------- 新消息 ----------

    [TestMethod]
    public void NewMessage_AppendsAndReturnsTheNewEntry()
    {
        var library = Sample();

        var entry = library.NewMessage(10, 3, "S10F3");

        Assert.HasCount(4, library.Messages);
        Assert.AreEqual(3, entry.Index);
        Assert.AreSame(entry, library.Messages[^1]);
        Assert.AreEqual(10, entry.Stream);
        Assert.AreEqual(3, entry.Function);
        Assert.AreEqual("S10F3", entry.Name);
        Assert.AreEqual("S10F3", entry.Signature);
        Assert.IsFalse(entry.WBit);
        Assert.IsNull(entry.AutoReply);
        Assert.IsNull(StructureNode.FromStructure(entry.Structure));
    }

    [TestMethod]
    public void NewMessage_SortsIntoTheQueryAndCanBeFilledIn()
    {
        var library = Sample();
        var entry = library.NewMessage(2, 41, "S2F41_HostCommandSend");

        Assert.AreEqual("S2F41_HostCommandSend", library.Query("2 41").Single().Name);
        Assert.AreEqual("S2F41_HostCommandSend", library.Query("S2F41").Single().Name);
        Assert.AreEqual(2, library.Query(null)[2].Stream, "排序后应该落在 S1F2 和 S7F25 之间");

        // 空结构 → 根 L → 一个 A 一个 U4，然后能预览成 SML
        Assert.IsNotNull(StructureEditor.AddChild(entry.Structure!, SecsItemType.List));
        var root = StructureNode.FromStructure(entry.Structure)!.Element;
        Assert.IsNotNull(StructureEditor.AddSibling(
            StructureEditor.AddChild(root, SecsItemType.A)!, SecsItemType.U4));

        Assert.IsTrue(SmlConverter.TryToSml(library.Messages[entry.Index], out var sml, out var error), error);
        StringAssert.StartsWith(sml, "'S2F41'");
        StringAssert.Contains(sml, "<L [2] ");
        StringAssert.Contains(sml, "<A [0] ''>");
        StringAssert.Contains(sml, "<U4 [1] 0>");
    }

    [TestMethod]
    public void NewMessage_WorksOnAnEmptyLibrary()
    {
        var library = MessageLibrary.Empty;
        Assert.IsEmpty(library.Messages);

        var entry = library.NewMessage(1, 1, "S1F1");

        Assert.HasCount(1, library.Messages);
        Assert.AreEqual("S1F1", entry.Name);
        StringAssert.Contains(library.Export(), "<Message Name=\"S1F1\">");
    }

    [TestMethod]
    public void NewMessage_KeepsTheExportLayoutReadable()
    {
        var library = Sample();
        library.NewMessage(10, 3, "S10F3");

        var exported = library.Export();
        var closing = exported.Split('\n').First(line => line.Contains("</MessageDictionary>"));
        Assert.AreEqual("</MessageDictionary>", closing.Trim(), "新消息不该和结尾标签挤在一行");
        StringAssert.Contains(exported, "\n        <Message Name=\"S10F3\">");
    }

    [TestMethod]
    public void NewMessage_RejectsStreamAndFunctionOutOfRange()
    {
        var library = Sample();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.NewMessage(0, 1, "x"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.NewMessage(256, 1, "x"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => library.NewMessage(1, 300, "x"));
        Assert.HasCount(3, library.Messages);
    }

    // ---------- 改名字 ----------

    [TestMethod]
    public void SetName_WritesTheAttributeAndClearsItOnEmpty()
    {
        var root = Root(Sample());
        var ppid = root.Elements().First();
        Assert.AreEqual("PPID", ppid.Attribute("Name")!.Value);

        StructureEditor.SetName(ppid, "RECIPE_ID");
        Assert.AreEqual("RECIPE_ID", ppid.Attribute("Name")!.Value);

        StructureEditor.SetName(ppid, "");
        Assert.IsNull(ppid.Attribute("Name"));
        StructureEditor.SetName(ppid, "   ");
        Assert.IsNull(ppid.Attribute("Name"), "全空白也算没名字");
    }

    // ---------- 改值 ----------

    [TestMethod]
    public void SetValue_RewritesTheValueElementAndLeavesMapsAlone()
    {
        var root = Root(Sample());
        var mask = root.Elements().ElementAt(2);

        Assert.IsTrue(StructureEditor.SetValue(mask, "1A2B"));

        Assert.AreEqual("1A2B", mask.Element("Value")!.Value);
        Assert.AreEqual("1024", mask.Element("Maps")!.Attribute("Type")!.Value);
        Assert.IsNull(new StructureNode(mask).ValidationError);
    }

    [TestMethod]
    public void SetValue_KeepsMultiLineTextAsTyped()
    {
        var library = Sample();
        var body = Root(library).Elements().ElementAt(1);
        Assert.AreEqual("PPBODY", body.Attribute("Name")!.Value);

        Assert.IsTrue(StructureEditor.SetValue(body, "[New]\nA=1\n"));

        Assert.AreEqual("[New]\nA=1\n", body.Element("Value")!.Value.Replace("\r\n", "\n"));
    }

    [TestMethod]
    public void SetValue_RefusesListsArraysAndUnknownElements()
    {
        var root = Root(Sample());
        Assert.IsFalse(StructureEditor.SetValue(root, "x"), "L 没有值");
        Assert.IsFalse(root.Elements().Any(element => element.Name == "Value"));

        var count = root.Elements().ElementAt(4);
        count.Add(new XElement("Value", "1"));
        Assert.IsFalse(StructureEditor.SetValue(count, "2"), "数组不可编辑");
        Assert.HasCount(2, count.Elements("Value"));

        var unknown = new XElement("Weird");
        Assert.IsFalse(StructureEditor.SetValue(unknown, "3"));
        Assert.IsFalse(unknown.HasElements);
    }

    [TestMethod]
    public void SetValue_AddsTheMissingValueElementInFrontOfMaps()
    {
        var element = new XElement("U4", new XElement("Maps", new XAttribute("Type", 64)));

        Assert.IsTrue(StructureEditor.SetValue(element, "7"));

        CollectionAssert.AreEqual(
            new[] { "Value", "Maps" },
            element.Elements().Select(child => child.Name.LocalName).ToArray());
        Assert.AreEqual("7", element.Element("Value")!.Value);
    }

    [TestMethod]
    public void IsValueEditable_MatchesWhatTheEditorCanTouch()
    {
        var root = Root(Sample());
        Assert.IsFalse(new StructureNode(root).IsValueEditable, "L 没有值");
        Assert.IsTrue(new StructureNode(root.Elements().First()).IsValueEditable);

        var count = root.Elements().ElementAt(4);
        count.Add(new XElement("Value", "1"));
        Assert.IsFalse(new StructureNode(count).IsValueEditable, "数组不可编辑");
        Assert.IsFalse(new StructureNode(new XElement("Weird")).IsValueEditable, "未知类型不碰");
    }

    [TestMethod]
    public void SetValue_AnOutOfRangeValueIsFlaggedAndBlocksTheSmlPreview()
    {
        var library = Sample();
        var count = Root(library).Elements().ElementAt(4);

        Assert.IsTrue(StructureEditor.SetValue(count, "4294967296"), "U4 的上限是 2^32-1");
        Assert.IsNotNull(new StructureNode(count).ValidationError);
        Assert.IsFalse(SmlConverter.TryToSml(library.Messages[2], out _, out var error), "越界值不该能转 SML");
        StringAssert.Contains(error!, "4294967296");

        Assert.IsTrue(StructureEditor.SetValue(count, "42"));
        Assert.IsTrue(SmlConverter.TryToSml(library.Messages[2], out var sml, out error), error);
        StringAssert.Contains(sml, "<U4 [1] 42>");
    }
}
