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
            Assert.AreEqual(string.Empty, node.ValueText, type.ToString());
        }

        var boolean = new StructureNode(StructureEditor.NewElement(SecsItemType.Boolean));
        Assert.IsNull(boolean.ValidationError);
        Assert.AreEqual("False", boolean.ValueText);

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
    public void SetValue_RefusesListsAndUnknownButFoldsArraysIntoOneValue()
    {
        var root = Root(Sample());
        Assert.IsFalse(StructureEditor.SetValue(root, "x"), "L 没有值");
        Assert.IsFalse(root.Elements().Any(element => element.Name == "Value"));

        var count = root.Elements().ElementAt(4);
        count.Add(new XElement("Value", "1"));

        Assert.IsTrue(StructureEditor.SetValue(count, "2 3"), "多个值也算可编辑");
        Assert.HasCount(1, count.Elements("Value"), "改完只能留一个 <Value>，第二种形状不能留在文件里");
        Assert.AreEqual("2 3", count.Element("Value")!.Value);

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
        Assert.IsTrue(new StructureNode(count).IsValueEditable, "多个值也算可编辑");
        Assert.IsTrue(StructureEditor.SetValue(count, "1 2"), "输入框显示得出来，这里就必须改得进去");
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

    // ---------- 复制 ----------

    [TestMethod]
    public void CloneSubtree_IsDeepAndDetached()
    {
        var root = Root(Sample());
        var ppid = root.Elements().First();

        var clone = StructureEditor.CloneSubtree(root);

        Assert.IsNull(clone.Parent, "克隆体不能挂在任何树上");
        Assert.HasCount(root.Elements().Count(), clone.Elements());
        Assert.IsFalse(ReferenceEquals(ppid, clone.Elements().First()), "浅拷贝会让两份共用同一个子节点实例");

        StructureEditor.SetName(clone.Elements().First(), "改的是克隆");
        Assert.AreEqual("PPID", ppid.Attribute("Name")!.Value, "改克隆不能连着改掉原节点");
    }

    [TestMethod]
    public void CloneSubtree_CarriesMapsAndKeepsTheSourceText()
    {
        var leaf = Root(Sample()).Elements().ElementAt(4);
        Assert.IsNotNull(leaf.Element("Maps"));

        var clone = StructureEditor.CloneSubtree(leaf);

        Assert.AreEqual(
            leaf.ToString(SaveOptions.DisableFormatting),
            clone.ToString(SaveOptions.DisableFormatting),
            "Maps、属性和多行 Value 都得原样带走");
    }

    [TestMethod]
    public void CloneSubtree_OfAWholeRootStaysSendable()
    {
        var library = Sample();

        var clone = StructureEditor.CloneSubtree(Root(library));
        var pasted = StructureEditor.InsertClone(clone, library.Messages[0].Structure!, asChild: true);

        Assert.IsNotNull(pasted);
        Assert.IsTrue(SmlConverter.TryToSml(library.Messages[0], out _, out var error), error);
    }

    // ---------- 粘贴落点 ----------

    [TestMethod]
    public void PlacementOf_AgreesWithWhatTheInsertMethodsAccept()
    {
        var library = Sample();
        var structure = library.Messages[0].Structure!;
        var rooted = library.Messages[1].Structure!;
        var root = Root(library);
        var leaf = root.Elements().ElementAt(4);

        var positions = new[]
        {
            structure,                      // 空 Structure：收，收进来的是根
            rooted,                         // 已有根的 Structure：既不是 L 也没有同级可当
            root,                           // L：落进去当子项
            leaf,                           // U4：不能收子项，退一步当它的同级
            leaf.Element("Value")!,         // Value：两档都不行
            leaf.Element("Maps")!           // Maps：两档都不行
        };
        var expected = new[]
        {
            PastePlacement.AsChild,
            PastePlacement.Nowhere,
            PastePlacement.AsChild,
            PastePlacement.AsSibling,
            PastePlacement.Nowhere,
            PastePlacement.Nowhere
        };

        for (var i = 0; i < positions.Length; i++)
        {
            var placement = StructureEditor.PlacementOf(positions[i]);
            Assert.AreEqual(expected[i], placement, positions[i].Name.LocalName);

            var clone = StructureEditor.CloneSubtree(root.Elements().First());
            var pasted = StructureEditor.PasteInto(clone, positions[i]);
            Assert.AreEqual(placement != PastePlacement.Nowhere, pasted is not null, positions[i].Name.LocalName);
        }
    }

    [TestMethod]
    public void PasteInto_AnEmptyStructureReceivesTheCloneAsTheRoot()
    {
        var library = Sample();
        var structure = library.Messages[0].Structure!;

        var pasted = StructureEditor.PasteInto(StructureEditor.CloneSubtree(Root(library)), structure);

        Assert.IsNotNull(pasted);
        Assert.AreSame(pasted, StructureNode.FromStructure(structure)!.Element);
    }

    [TestMethod]
    public void PasteInto_ADataTargetReceivesTheCloneAsItsSibling()
    {
        var root = Root(Sample());
        var ppid = root.Elements().First();

        var pasted = StructureEditor.PasteInto(StructureEditor.CloneSubtree(root), ppid);

        Assert.IsNotNull(pasted);
        Assert.AreSame(pasted, ppid.ElementsAfterSelf().First(), "同级那一步要紧跟在选中项后面");
        Assert.HasCount(6, root.Elements());
    }

    [TestMethod]
    public void PasteInto_LeavesTheTreeAloneWhenThePositionRefusesIt()
    {
        var library = Sample();
        var rooted = library.Messages[1].Structure!;
        var value = Root(library).Elements().ElementAt(4).Element("Value")!;

        Assert.IsNull(StructureEditor.PasteInto(StructureEditor.CloneSubtree(Root(library)), rooted), "一条消息只有一个根");
        Assert.IsNull(StructureEditor.PasteInto(StructureEditor.CloneSubtree(value), value));
        Assert.HasCount(1, rooted.Elements());
    }

    [TestMethod]
    public void InsertClone_RejectsALiveNodeFromTheTree()
    {
        var root = Root(Sample());

        // 粘的必须是克隆体。递进去一个活节点等于把原节点搬走，这条要在 Core 就拦下。
        Assert.ThrowsExactly<ArgumentException>(
            () => StructureEditor.InsertClone(root.Elements().First(), root, asChild: true));
        Assert.HasCount(5, root.Elements());
    }

    [TestMethod]
    public void PasteInto_ReachesExportAndLeavesTheUnknownContentAlone()
    {
        var library = Sample();

        Assert.IsNotNull(StructureEditor.PasteInto(
            StructureEditor.CloneSubtree(Root(library)),
            library.Messages[0].Structure!));

        var reloaded = MessageLibrary.Load(library.Export());
        Assert.IsNotNull(StructureNode.FromStructure(reloaded.Messages[0].Structure));
        Assert.AreEqual("keep me", reloaded.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry")!.Value);
        StringAssert.Contains(reloaded.Export(), "注释必须原样保留");
    }

    // ---------- 拖动搬迁 ----------

    /// <summary>
    /// 自己搭一棵树：根 L 下挂 first(A)、mid(L){ deep(U4)，deep 带 Maps }、last(U4)。
    /// 挂进 XDocument 是让 DropPlacementOf 那句「必须在同一份文档里」有得可比。
    /// </summary>
    private static XElement BuildTreeRoot()
    {
        var structure = new XElement("Structure",
            new XElement("L", new XAttribute("Name", "root"),
                new XElement("A", new XAttribute("Name", "first"), new XElement("Value", "1")),
                new XElement("L", new XAttribute("Name", "mid"),
                    new XElement("U4", new XAttribute("Name", "deep"),
                        new XElement("Value", "2"),
                        new XElement("Maps", new XAttribute("Type", 64)))),
                new XElement("U4", new XAttribute("Name", "last"), new XElement("Value", "3"))));
        _ = new XDocument(structure);

        return structure.Element("L")!;
    }

    private static XElement Child(XElement parent, string name) =>
        parent.Elements().First(child => (string?)child.Attribute("Name") == name);

    private static string[] ChildNames(XElement parent) =>
        [.. parent.Elements().Select(child => (string?)child.Attribute("Name") ?? child.Name.LocalName)];

    [TestMethod]
    public void CanMove_RefusesTheRootAndEverythingWithoutASiblingPosition()
    {
        var root = BuildTreeRoot();
        var mid = Child(root, "mid");
        var deep = Child(mid, "deep");

        Assert.IsTrue(StructureEditor.CanMove(Child(root, "first")));
        Assert.IsTrue(StructureEditor.CanMove(Child(root, "last")));
        Assert.IsTrue(StructureEditor.CanMove(mid));
        Assert.IsTrue(StructureEditor.CanMove(deep), "deep 的父是 L，它就有同级位置");
        Assert.IsFalse(StructureEditor.CanMove(root), "根只能被落进，不参与拖动");
        Assert.IsFalse(StructureEditor.CanMove(Child(root, "first").Element("Value")!));
        Assert.IsFalse(StructureEditor.CanMove(deep.Element("Maps")!));
    }

    [TestMethod]
    public void DropPlacementOf_TheRootIsAValidTargetAndReceivesTheNodeAsAChild()
    {
        var root = BuildTreeRoot();

        Assert.AreEqual(PastePlacement.AsChild, StructureEditor.DropPlacementOf(Child(root, "last"), root));
    }

    [TestMethod]
    public void DropPlacementOf_FallsToSiblingWhenTheTargetCannotTakeChildren()
    {
        var root = BuildTreeRoot();

        Assert.AreEqual(
            PastePlacement.AsSibling,
            StructureEditor.DropPlacementOf(Child(root, "first"), Child(root, "last")));
        Assert.AreEqual(
            PastePlacement.Nowhere,
            StructureEditor.DropPlacementOf(Child(root, "first"), Child(root, "first").Element("Value")!),
            "Value 既不收子项也没有同级位置，光标就该显示不能放");
    }

    [TestMethod]
    public void DropPlacementOf_RefusesSelfAndOwnDescendants()
    {
        var root = BuildTreeRoot();
        var mid = Child(root, "mid");
        var deep = Child(mid, "deep");

        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(mid, mid), "不能拖到自己身上");
        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(mid, deep), "拖进自己的后代就是自嵌套");
        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(root, deep), "根本来就不能动");
    }

    [TestMethod]
    public void DropPlacementOf_RefusesNodesFromDifferentDocuments()
    {
        var dragged = Child(BuildTreeRoot(), "first");
        var target = Child(BuildTreeRoot(), "mid");

        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(dragged, target));
    }

    [TestMethod]
    public void Move_IntoAListAppendsAsTheLastChildWithoutCopying()
    {
        var root = BuildTreeRoot();
        var first = Child(root, "first");
        var mid = Child(root, "mid");
        var deep = Child(mid, "deep");

        Assert.IsTrue(StructureEditor.Move(first, mid));

        CollectionAssert.AreEqual(new[] { "mid", "last" }, ChildNames(root), "搬走不是复制，原来的位置该少一个");
        CollectionAssert.AreEqual(new[] { "deep", "first" }, ChildNames(mid));
        Assert.IsTrue(ReferenceEquals(first, mid.Elements().Last()));
        Assert.IsNotNull(deep.Element("Maps"), "搬动的子树里的 Maps 不能掉");
    }

    [TestMethod]
    public void Move_AfterASiblingInsertsRightBehindIt()
    {
        var root = BuildTreeRoot();

        Assert.IsTrue(StructureEditor.Move(Child(root, "first"), Child(root, "last")));

        CollectionAssert.AreEqual(new[] { "mid", "last", "first" }, ChildNames(root));
    }

    [TestMethod]
    public void DropPlacementOf_ZonesChooseBeforeInsideAndAfter()
    {
        var root = BuildTreeRoot();
        var first = Child(root, "first");
        var mid = Child(root, "mid");

        Assert.AreEqual(PastePlacement.AsSiblingBefore, StructureEditor.DropPlacementOf(first, mid, DropZone.Top));
        Assert.AreEqual(PastePlacement.AsChild, StructureEditor.DropPlacementOf(first, mid, DropZone.Middle));
        Assert.AreEqual(PastePlacement.AsSibling, StructureEditor.DropPlacementOf(first, mid, DropZone.Bottom));
    }

    /// <summary>根元素只能被落进去：上段和下段都要求有同级位置，那两档对它不成立。</summary>
    [TestMethod]
    public void DropPlacementOf_TopAndBottomRefuseTheRoot()
    {
        var root = BuildTreeRoot();
        var first = Child(root, "first");

        Assert.AreEqual(PastePlacement.AsChild, StructureEditor.DropPlacementOf(first, root, DropZone.Middle));
        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(first, root, DropZone.Top));
        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(first, root, DropZone.Bottom));
    }

    [TestMethod]
    public void Move_TopAndBottomLandOnOppositeSidesOfTheTarget()
    {
        var before = BuildTreeRoot();
        Assert.IsTrue(StructureEditor.Move(Child(before, "last"), Child(before, "first"), DropZone.Top));
        CollectionAssert.AreEqual(new[] { "last", "first", "mid" }, ChildNames(before));

        var after = BuildTreeRoot();
        Assert.IsTrue(StructureEditor.Move(Child(after, "last"), Child(after, "first"), DropZone.Bottom));
        CollectionAssert.AreEqual(new[] { "first", "last", "mid" }, ChildNames(after));
    }

    [TestMethod]
    public void Move_LeavesTheTreeAloneWhenTheLandingSpotRefusesIt()
    {
        var root = BuildTreeRoot();
        var before = root.ToString(SaveOptions.DisableFormatting);
        var mid = Child(root, "mid");

        Assert.IsFalse(StructureEditor.Move(mid, Child(mid, "deep")));
        Assert.IsFalse(StructureEditor.Move(root, Child(root, "last")));
        Assert.AreEqual(before, root.ToString(SaveOptions.DisableFormatting));
    }

    [TestMethod]
    public void DropPlacementOf_AgreesWithMoveOnEveryPair()
    {
        var names = new[] { "root", "first", "mid", "deep", "last", "Value", "Maps" };

        foreach (var draggedName in names)
        {
            foreach (var targetName in names)
            {
                var root = BuildTreeRoot();
                var dragged = Pick(root, draggedName);
                var target = Pick(root, targetName);
                var before = root.ToString(SaveOptions.DisableFormatting);

                var placement = StructureEditor.DropPlacementOf(dragged, target);
                var moved = StructureEditor.Move(dragged, target);

                Assert.AreEqual(placement != PastePlacement.Nowhere, moved, $"{draggedName} -> {targetName}");
                if (!moved)
                {
                    Assert.AreEqual(before, root.ToString(SaveOptions.DisableFormatting), $"{draggedName} -> {targetName}");
                }
            }
        }

        static XElement Pick(XElement root, string name) => name switch
        {
            "root" => root,
            "first" => Child(root, "first"),
            "mid" => Child(root, "mid"),
            "deep" => Child(Child(root, "mid"), "deep"),
            "last" => Child(root, "last"),
            "Value" => Child(root, "first").Element("Value")!,
            "Maps" => Child(Child(root, "mid"), "deep").Element("Maps")!,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "测试自己写错了名字")
        };
    }

    [TestMethod]
    public void Move_ReachesExportAndKeepsTheMessageSendable()
    {
        var library = Sample();
        var root = Root(library);
        var ppid = root.Elements().First();
        Assert.AreEqual("PPID", (string?)ppid.Attribute("Name"));
        var count = root.Elements().ElementAt(4);

        Assert.IsTrue(StructureEditor.Move(ppid, count));

        var reloaded = MessageLibrary.Load(library.Export());
        var reloadedRoot = StructureNode.FromStructure(reloaded.Messages[2].Structure)!;
        Assert.HasCount(5, reloadedRoot.Children, "搬走不是复制，总数不该变多");
        Assert.AreEqual("PPID", reloadedRoot.Children.Last().Name);
        Assert.IsTrue(SmlConverter.TryToSml(reloaded.Messages[2], out _, out var error), error);
        Assert.AreEqual("keep me", reloaded.Document.Root!.Element("DataDictionary")!.Element("UnknownEntry")!.Value);
    }

    // ---------- 段归属：消息段和字典段之间只开复制 ----------

    /// <summary>
    /// 一份消息库的 XML 里 <c>&lt;DataDictionary&gt;</c> 和 <c>&lt;MessageDictionary&gt;</c>
    /// 挂在同一个根下面，所以「是不是同一份文档」分不开两段。这几条用一个真库：
    /// 字典里建一个 L 条目并给它一个子项，消息那边取 RecipeBody 的根 L。
    /// </summary>
    private static (XElement Entry, XElement EntryChild, XElement MessageRoot) TwoSections()
    {
        var library = Sample();
        var entry = library.NewEntry("清单", SecsItemType.List);
        var entryChild = StructureEditor.AddChild(entry.Element, SecsItemType.U4)!;

        return (entry.Element, entryChild, Root(library));
    }

    [TestMethod]
    public void SectionOf_TakesTheNearestSectionAncestor()
    {
        var (entry, entryChild, messageRoot) = TwoSections();

        Assert.AreEqual(SecsSection.Dictionary, SecsSections.Of(entry));
        Assert.AreEqual(SecsSection.Dictionary, SecsSections.Of(entryChild));
        Assert.AreEqual(SecsSection.Message, SecsSections.Of(messageRoot));
        Assert.AreEqual(SecsSection.Message, SecsSections.Of(messageRoot.Elements().First()));
        Assert.AreEqual(
            SecsSection.Elsewhere,
            SecsSections.Of(StructureEditor.CloneSubtree(entry)),
            "脱离文档的克隆还没落地，算别处");
        Assert.AreEqual(SecsSection.Elsewhere, SecsSections.Of(BuildTreeRoot()));
        Assert.AreEqual(
            SecsSection.Dictionary,
            SecsSections.Of(Sample().Document.Root!.Element("DataDictionary")!),
            "段落元素自己也算这一段");
    }

    /// <summary>字典段当列表容器：条目有同级位置、收得下子项，功能三那套落点判断直接继承过来。</summary>
    [TestMethod]
    public void TheDictionaryIsAContainerSoEntriesTakeSiblingsAndChildren()
    {
        var (entry, entryChild, _) = TwoSections();

        Assert.IsTrue(StructureEditor.CanAddChild(entry), "L 条目收得下子项");
        Assert.IsTrue(StructureEditor.CanAddSibling(entry), "条目这一层有同级位置");
        Assert.IsTrue(StructureEditor.CanMove(entry));
        Assert.AreEqual(PastePlacement.AsChild, StructureEditor.PlacementOf(entry));

        var sibling = StructureEditor.AddSibling(entry, SecsItemType.U4);

        Assert.IsNotNull(sibling);
        Assert.IsTrue(ReferenceEquals(entry.NextNode, sibling), "同级就落在它后面");
        Assert.AreEqual(SecsSection.Dictionary, SecsSections.Of(sibling!));
    }

    /// <summary>跨段一律拦下，拦下来那份文档一个字都不动。</summary>
    [TestMethod]
    public void DropPlacementOf_RefusesEveryCrossSectionLandingSpot()
    {
        var library = Sample();
        var entry = library.NewEntry("清单", SecsItemType.List);
        var entryChild = StructureEditor.AddChild(entry.Element, SecsItemType.U4)!;
        var messageRoot = Root(library);
        var messageChild = messageRoot.Elements().First();
        var before = library.Export();

        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(messageChild, entry.Element));
        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(messageChild, entryChild));
        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(entry.Element, messageChild));
        Assert.AreEqual(PastePlacement.Nowhere, StructureEditor.DropPlacementOf(entryChild, messageRoot));
        Assert.AreEqual(
            PastePlacement.Nowhere,
            StructureEditor.DropPlacementOf(messageChild, entry.Element, DropZone.Top),
            "上段也不能把消息里的节点插进字典");
        Assert.AreEqual(
            PastePlacement.Nowhere,
            StructureEditor.DropPlacementOf(entryChild, messageRoot, DropZone.Bottom),
            "下段也不能把字典里的节点插进消息");

        Assert.IsFalse(StructureEditor.Move(messageChild, entry.Element));
        Assert.IsFalse(StructureEditor.Move(entry.Element, messageRoot));
        Assert.IsFalse(StructureEditor.Move(entryChild, messageRoot));

        Assert.AreEqual(before, library.Export(), "拦下来之后不能留下搬了一半的痕迹");
        Assert.HasCount(5, messageRoot.Elements().ToList(), "消息里没有因此少掉一个节点");
    }

    /// <summary>同段之内照旧能搬：字典里两条条目互相排序不拦。</summary>
    [TestMethod]
    public void Move_StillWorksInsideTheDictionary()
    {
        var library = Sample();
        var first = library.NewEntry("第一条", SecsItemType.U4);
        var second = library.NewEntry("第二条", SecsItemType.U4);

        Assert.AreEqual(
            PastePlacement.AsSibling,
            StructureEditor.DropPlacementOf(first.Element, second.Element));
        Assert.IsTrue(StructureEditor.Move(first.Element, second.Element));

        // 读文档里的活节点，不读 library.DataEntries：StructureEditor 改的是 XElement，
        // 条目列表是改动之前解析的那一份，读它只会测出自建的一份陈旧快照。
        CollectionAssert.AreEqual(
            new[] { "第二条", "第一条" },
            first.Element.Parent!.Elements()
                .Select(element => (string?)element.Attribute("Name"))
                .Where(name => name is "第一条" or "第二条")
                .ToList());
    }

    /// <summary>复制粘贴是唯一的跨段通道：粘进去的是克隆，两段各留一份。</summary>
    [TestMethod]
    public void PasteInto_AllowsTheTwoDirectionsAcrossSections()
    {
        var (entry, entryChild, messageRoot) = TwoSections();
        var messageChild = messageRoot.Elements().First();

        var intoDictionary = StructureEditor.PasteInto(
            StructureEditor.CloneSubtree(messageChild), entry);
        var intoMessage = StructureEditor.PasteInto(
            StructureEditor.CloneSubtree(entryChild), messageRoot);

        Assert.IsNotNull(intoDictionary);
        Assert.IsNotNull(intoMessage);
        Assert.AreNotSame(messageChild, intoDictionary);
        Assert.AreNotSame(entryChild, intoMessage);
        Assert.AreEqual(SecsSection.Message, SecsSections.Of(messageChild), "来源还挂在原来那一段");
        Assert.AreEqual(SecsSection.Dictionary, SecsSections.Of(entry), "目标条目也没换段");
    }

    // ---------- 字典页拖动：跨条目给副本，条目内搬 ----------

    /// <summary>
    /// 字典里搭两条条目：<c>清单(L){ 数量(U4)+Maps、标记(A) }</c> 和
    /// <c>配置(L){ 名称(A)、分组(L){ 组内(U4) } }</c>。顶层条目直接挂在
    /// &lt;DataDictionary&gt; 下面，正是字典页那棵树的第一层。
    /// </summary>
    private sealed record DictionaryFixture(
        MessageLibrary Library,
        XElement List,
        XElement Count,
        XElement Flag,
        XElement Config,
        XElement ConfigName,
        XElement Group,
        XElement InGroup)
    {
        public XElement Entries => List.Parent!;
    }

    private static DictionaryFixture BuildDictionary()
    {
        var library = Sample();
        var list = library.NewEntry("清单", SecsItemType.List).Element;
        var count = StructureEditor.AddChild(list, SecsItemType.U4)!;
        StructureEditor.SetName(count, "数量");
        count.Add(new XElement("Maps", new XAttribute("Type", 64)));
        var flag = StructureEditor.AddChild(list, SecsItemType.A)!;
        StructureEditor.SetName(flag, "标记");

        var config = library.NewEntry("配置", SecsItemType.List).Element;
        var configName = StructureEditor.AddChild(config, SecsItemType.A)!;
        StructureEditor.SetName(configName, "名称");
        var group = StructureEditor.AddChild(config, SecsItemType.List)!;
        var inGroup = StructureEditor.AddChild(group, SecsItemType.U4)!;
        StructureEditor.SetName(inGroup, "组内");

        return new DictionaryFixture(library, list, count, flag, config, configName, group, inGroup);
    }

    [TestMethod]
    public void DropWayOf_MovesEverythingInsideOneEntry()
    {
        var f = BuildDictionary();

        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(f.Count, f.Flag), "同一条目里换顺序");
        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(f.Count, f.List), "回到自己待的那一层");
        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(f.ConfigName, f.Group), "同一条目里往下挪一层");
        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(f.InGroup, f.Config), "同一条目里往上挪一层");
    }

    [TestMethod]
    public void DropWayOf_CopiesAcrossEntriesAndMovesAtTheDictionaryRoot()
    {
        var f = BuildDictionary();
        var plain = f.Library.NewEntry("一个值", SecsItemType.U4).Element;

        Assert.AreEqual(DropWay.Copy, StructureEditor.DropWayOf(f.Count, f.Config), "别的条目里的东西落到这个条目底下");
        Assert.AreEqual(DropWay.Copy, StructureEditor.DropWayOf(f.Count, f.ConfigName), "同级落点也一样：它要挪进另一条");
        Assert.AreEqual(DropWay.Copy, StructureEditor.DropWayOf(f.List, f.Config), "整条拖进另一条，原条目该还在顶层");
        Assert.AreEqual(
            DropWay.Move,
            StructureEditor.DropWayOf(f.List, plain),
            "两条顶层条目在字典那一层排队，谁都不进谁的肚子里，还是搬");
    }

    [TestMethod]
    public void DropWayOf_KeepsMoveOrCopyWhenTheZoneIsTopOrBottom()
    {
        var f = BuildDictionary();
        var plain = f.Library.NewEntry("一个值", SecsItemType.U4).Element;

        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(f.Count, f.Flag, DropZone.Top), "同一条目里换顺序");
        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(f.Count, f.Flag, DropZone.Bottom));
        Assert.AreEqual(DropWay.Copy, StructureEditor.DropWayOf(f.Count, f.Config, DropZone.Top), "跨条目仍然是复制");
        Assert.AreEqual(DropWay.Copy, StructureEditor.DropWayOf(f.Count, f.Config, DropZone.Bottom));
        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(f.List, plain, DropZone.Top), "字典顶层排队还是搬");
    }

    [TestMethod]
    public void CopyForDrop_InsertsTheCloneOnEitherSideAndLeavesTheSource()
    {
        var f = BuildDictionary();

        var before = StructureEditor.CopyForDrop(f.Count, f.ConfigName, DropZone.Top);
        Assert.IsNotNull(before);
        Assert.IsTrue(ReferenceEquals(before, f.ConfigName.PreviousNode), "上段插到目标前面");
        Assert.IsNotNull(f.Count.Parent, "来源那一条一个字不少");

        var after = StructureEditor.CopyForDrop(f.Count, f.ConfigName, DropZone.Bottom);
        Assert.IsNotNull(after);
        Assert.IsTrue(ReferenceEquals(after, f.ConfigName.NextNode), "下段插在目标后面");
        Assert.AreEqual("数量", (string?)f.Count.Attribute("Name"), "来源那条的名字没变");
        Assert.IsTrue(ReferenceEquals(f.Count.Parent, f.List), "来源还挂在原来的条目里");
    }

    [TestMethod]
    public void DropWayOf_MovesAcrossContainersInTheMessageTree()
    {
        var library = Sample();
        var root = Root(library);
        var ppid = root.Elements().First();
        var nested = StructureEditor.AddChild(root, SecsItemType.List)!;

        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(ppid, nested), "消息那棵树是一层一层的，跨层也搬");
        Assert.AreEqual(DropWay.Move, StructureEditor.DropWayOf(ppid, root));
        Assert.AreEqual(
            DropWay.Nowhere,
            StructureEditor.DropWayOf(ppid, library.Document.Root!.Element("DataDictionary")!.Elements().First()),
            "跨段照旧不收");
    }

    [TestMethod]
    public void CopyForDrop_LeavesTheSourceEntryUntouched()
    {
        var f = BuildDictionary();
        var rootEntries = f.Entries.Elements().Count();

        var copy = StructureEditor.CopyForDrop(f.Count, f.Config);

        Assert.IsNotNull(copy);
        Assert.IsFalse(ReferenceEquals(copy, f.Count), "插下去的必须是另一份");
        Assert.AreSame(f.List, f.Count.Parent, "原来那份还挂在来源条目里");
        Assert.IsTrue(ReferenceEquals(copy, f.Config.Elements().Last()));
        Assert.IsNotNull(copy!.Element("Maps"), "副本得把 Maps 一起带走");
        Assert.HasCount(2, f.List.Elements(), "来源条目不少也不多");
        Assert.HasCount(rootEntries, f.Entries.Elements(), "整条复制进另一条不会多出顶层条目");

        StructureEditor.SetName(copy, "改的是副本");
        Assert.AreEqual("数量", (string?)f.Count.Attribute("Name"), "改副本不能连着改掉原本");
    }

    [TestMethod]
    public void CopyForDrop_WholeEntryCopyKeepsTheOriginalAtTheTopLevel()
    {
        var f = BuildDictionary();

        var copy = StructureEditor.CopyForDrop(f.List, f.Config);

        Assert.IsNotNull(copy);
        Assert.AreEqual("清单", (string?)copy!.Attribute("Name"));
        Assert.AreSame(f.Entries, f.List.Parent, "被复制的那一条还在顶层");
        Assert.AreSame(f.Config, copy.Parent, "副本落在配置里面");
        Assert.HasCount(2, copy.Elements(), "子树整份带走，不是只带个空壳");
    }

    [TestMethod]
    public void CopyAndMove_TakeTurnsOnEveryLandingSpot()
    {
        var names = new[] { "清单", "数量", "标记", "配置", "名称", "分组", "组内" };

        foreach (var draggedName in names)
        {
            foreach (var targetName in names)
            {
                var f = BuildDictionary();
                var dragged = Pick(f, draggedName);
                var target = Pick(f, targetName);
                var before = f.Entries.ToString(SaveOptions.DisableFormatting);
                var beforeCount = f.Entries.DescendantsAndSelf().Count();
                var sourceSize = dragged.DescendantsAndSelf().Count();

                var way = StructureEditor.DropWayOf(dragged, target);
                Assert.AreEqual(
                    StructureEditor.DropPlacementOf(dragged, target) != PastePlacement.Nowhere,
                    way != DropWay.Nowhere,
                    $"{draggedName} -> {targetName}");

                switch (way)
                {
                    case DropWay.Move:
                        Assert.IsTrue(StructureEditor.Move(dragged, target), $"{draggedName} -> {targetName}");
                        Assert.IsTrue(
                            f.Entries.Descendants().Any(node => ReferenceEquals(node, dragged)),
                            $"{draggedName} -> {targetName}：搬的得是同一个实例，还在这份字典里");
                        Assert.AreEqual(beforeCount, f.Entries.DescendantsAndSelf().Count(), "搬动不新增也不减少元素");
                        break;

                    case DropWay.Copy:
                        Assert.IsFalse(StructureEditor.Move(dragged, target), $"{draggedName} -> {targetName} 只准复制");
                        Assert.AreEqual(before, f.Entries.ToString(SaveOptions.DisableFormatting), "被拒的那一次不许留下痕迹");

                        var copy = StructureEditor.CopyForDrop(dragged, target);
                        Assert.IsNotNull(copy, $"{draggedName} -> {targetName}");
                        Assert.IsTrue(
                            f.Entries.Descendants().Any(node => ReferenceEquals(node, dragged)),
                            "复制这条路不能把原本挪走");
                        Assert.AreEqual(beforeCount + sourceSize, f.Entries.DescendantsAndSelf().Count(), "多出来的正好是一份副本");
                        break;

                    default:
                        Assert.IsFalse(StructureEditor.Move(dragged, target));
                        Assert.IsNull(StructureEditor.CopyForDrop(dragged, target));
                        Assert.AreEqual(before, f.Entries.ToString(SaveOptions.DisableFormatting));
                        break;
                }
            }
        }

        static XElement Pick(DictionaryFixture f, string name) => name switch
        {
            "清单" => f.List,
            "数量" => f.Count,
            "标记" => f.Flag,
            "配置" => f.Config,
            "名称" => f.ConfigName,
            "分组" => f.Group,
            "组内" => f.InGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "测试自己写错了名字")
        };
    }

    [TestMethod]
    public void CopyForDrop_ReturnsNullWhenTheLandingSpotIsAMove()
    {
        var f = BuildDictionary();
        var before = f.Entries.ToString(SaveOptions.DisableFormatting);

        Assert.IsNull(StructureEditor.CopyForDrop(f.Count, f.Flag), "同一条目里该搬，不该给副本");
        Assert.IsNull(StructureEditor.CopyForDrop(f.Count, f.Count));
        Assert.AreEqual(before, f.Entries.ToString(SaveOptions.DisableFormatting));
    }

    [TestMethod]
    public void DictionaryCopies_ReachExportAndReloadAsTwoSeparateEntries()
    {
        var f = BuildDictionary();
        Assert.IsNotNull(StructureEditor.CopyForDrop(f.Count, f.Config));

        var reloaded = MessageLibrary.Load(f.Library.Export());
        var list = reloaded.DataEntries.Single(entry => entry.Name == "清单").Element;
        var config = reloaded.DataEntries.Single(entry => entry.Name == "配置").Element;

        Assert.HasCount(2, list.Elements(), "来源条目没被这次复制削掉一个");
        Assert.HasCount(3, config.Elements(), "副本要落在配置里");
        Assert.IsFalse(ReferenceEquals(list.Elements().First(), config.Elements().Last()));
        StringAssert.Contains(f.Library.Export(), "<Maps Type=\"64\"");
    }

    [TestMethod]
    public void SyncDataEntries_FollowsATopLevelReorder()
    {
        var f = BuildDictionary();
        var plain = f.Library.NewEntry("一个值", SecsItemType.U4).Element;

        string DocumentOrder() =>
            string.Join(',', f.Entries.Elements().Select(element => (string?)element.Attribute("Name") ?? string.Empty));
        string SnapshotOrder() => string.Join(',', f.Library.DataEntries.Select(entry => entry.Name));

        Assert.IsTrue(
            StructureEditor.Move(f.List, plain),
            "把顶层的 L 排到一条 U4 之后：落点在同一层，是搬不是复制");

        var document = DocumentOrder();
        Assert.AreNotEqual(
            document,
            SnapshotOrder(),
            "只动了 XElement，条目清单不跟着变。界面那棵树照这份清单排行，不重拍就是老次序");

        f.Library.SyncDataEntries();

        Assert.AreEqual(document, SnapshotOrder(), "重拍之后清单和文档同序");
        CollectionAssert.AreEqual(
            Enumerable.Range(0, f.Library.DataEntries.Count).ToArray(),
            f.Library.DataEntries.Select(entry => entry.Index).ToArray(),
            "序号也按新次序重排，界面上认条目靠它");
    }
}
